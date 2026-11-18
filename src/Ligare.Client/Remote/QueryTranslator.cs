using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Ligare.Remote;

internal enum QueryTerminal
{
    /// <summary>Enumerate all results.</summary>
    List,
    First,
    FirstOrDefault,
    Single,
    SingleOrDefault,
    Any,
    Count,
    LongCount,
}

/// <summary>Navigations to load, with orderings from filtered includes applied after loading.</summary>
internal sealed class IncludeNode
{
    public Dictionary<string, IncludeNode> Children { get; } = new(StringComparer.Ordinal);

    public List<(LambdaExpression Key, bool Descending)> Orderings { get; } = [];

    public IncludeNode Child(string navigation)
    {
        if (!Children.TryGetValue(navigation, out var child))
        {
            child = new IncludeNode();
            Children[navigation] = child;
        }

        return child;
    }

    /// <summary><c>Status,StatusChanges($expand=Status)</c></summary>
    public string ToExpand() =>
        string.Join(",", Children.Select(c => c.Value.Children.Count == 0 ? c.Key : $"{c.Key}($expand={c.Value.ToExpand()})"));
}

internal sealed record RemoteQuery(
    Type ElementType,
    string? Filter,
    IReadOnlyList<string> OrderBy,
    int? Skip,
    int? Top,
    IncludeNode Includes,
    QueryTerminal Terminal,
    bool Tracking)
{
    /// <summary>The OData query string, without the leading '?'.</summary>
    public string ToQueryString(bool forCount = false)
    {
        var parts = new List<string>();
        if (Filter is not null)
        {
            parts.Add("$filter=" + Uri.EscapeDataString(Filter));
        }

        if (!forCount)
        {
            if (OrderBy.Count > 0)
            {
                parts.Add("$orderby=" + Uri.EscapeDataString(string.Join(",", OrderBy)));
            }

            if (Skip is { } skip)
            {
                parts.Add("$skip=" + skip.ToString(CultureInfo.InvariantCulture));
            }

            if (Top is { } top)
            {
                parts.Add("$top=" + top.ToString(CultureInfo.InvariantCulture));
            }

            if (Includes.Children.Count > 0)
            {
                parts.Add("$expand=" + Uri.EscapeDataString(Includes.ToExpand()));
            }
        }

        return string.Join("&", parts);
    }
}

/// <summary>
/// Turns an EF Core query expression into an OData request. Supports what can run on the server:
/// Where, OrderBy/ThenBy (descending), Skip, Take, Include/ThenInclude (with OrderBy inside),
/// AsNoTracking/AsTracking and the terminal operators First, Single, Any and Count. Everything else
/// fails with a <see cref="NotSupportedException"/> naming the operator, instead of silently running locally.
/// </summary>
internal static class QueryTranslator
{
    public static RemoteQuery Translate(Expression expression, bool trackingByDefault)
    {
        var state = new State(trackingByDefault);
        var source = expression;

        if (expression is MethodCallExpression { Method.DeclaringType: var declaring } terminalCall && declaring == typeof(Queryable)
            && TerminalOf(terminalCall.Method.Name) is { } terminal)
        {
            state.Terminal = terminal;
            source = terminalCall.Arguments[0];
            if (terminalCall.Arguments.Count == 2)
            {
                Visit(source, state);
                state.AddFilter(Unquote(terminalCall.Arguments[1]));
                return state.Build();
            }
        }

        Visit(source, state);
        return state.Build();
    }

    private static QueryTerminal? TerminalOf(string method) => method switch
    {
        nameof(Queryable.First) => QueryTerminal.First,
        nameof(Queryable.FirstOrDefault) => QueryTerminal.FirstOrDefault,
        nameof(Queryable.Single) => QueryTerminal.Single,
        nameof(Queryable.SingleOrDefault) => QueryTerminal.SingleOrDefault,
        nameof(Queryable.Any) => QueryTerminal.Any,
        nameof(Queryable.Count) => QueryTerminal.Count,
        nameof(Queryable.LongCount) => QueryTerminal.LongCount,
        _ => null,
    };

    private static void Visit(Expression expression, State state)
    {
        if (expression is RemoteRootExpression root)
        {
            state.ElementType = root.ElementType;
            return;
        }

        if (expression is not MethodCallExpression call)
        {
            throw new NotSupportedException($"Ligare cannot send '{expression}' to the server.");
        }

        Visit(call.Arguments[0], state);
        var name = call.Method.Name;

        if (call.Method.DeclaringType == typeof(Queryable))
        {
            switch (name)
            {
                case nameof(Queryable.Where):
                    state.EnsureNoPaging(name);
                    state.AddFilter(Unquote(call.Arguments[1]));
                    return;
                case nameof(Queryable.OrderBy):
                case nameof(Queryable.OrderByDescending):
                    state.EnsureNoPaging(name);
                    state.OrderBy.Clear();
                    state.OrderBy.Add(ODataFilterWriter.Path(Unquote(call.Arguments[1])) + (name == nameof(Queryable.OrderByDescending) ? " desc" : ""));
                    return;
                case nameof(Queryable.ThenBy):
                case nameof(Queryable.ThenByDescending):
                    state.OrderBy.Add(ODataFilterWriter.Path(Unquote(call.Arguments[1])) + (name == nameof(Queryable.ThenByDescending) ? " desc" : ""));
                    return;
                case nameof(Queryable.Skip):
                    if (state.Top is not null)
                    {
                        throw new NotSupportedException("Ligare sends Skip before Take only, e.g. Skip(20).Take(10).");
                    }

                    state.Skip = (state.Skip ?? 0) + Evaluate<int>(call.Arguments[1]);
                    return;
                case nameof(Queryable.Take):
                    var take = Evaluate<int>(call.Arguments[1]);
                    state.Top = state.Top is { } top ? Math.Min(top, take) : take;
                    return;
            }
        }
        else if (call.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions))
        {
            switch (name)
            {
                case nameof(EntityFrameworkQueryableExtensions.AsNoTracking):
                case nameof(EntityFrameworkQueryableExtensions.AsNoTrackingWithIdentityResolution):
                    state.Tracking = false;
                    return;
                case nameof(EntityFrameworkQueryableExtensions.AsTracking):
                    state.Tracking = call.Arguments.Count == 1 || Evaluate<QueryTrackingBehavior>(call.Arguments[1]) == QueryTrackingBehavior.TrackAll;
                    return;
                case nameof(EntityFrameworkQueryableExtensions.TagWith):
                    return;
                case nameof(EntityFrameworkQueryableExtensions.Include) when call.Arguments[1].Type == typeof(string):
                    var node = state.Includes;
                    foreach (var navigation in Evaluate<string>(call.Arguments[1]).Split('.'))
                    {
                        node = node.Child(navigation);
                    }

                    state.LastInclude = node;
                    return;
                case nameof(EntityFrameworkQueryableExtensions.Include):
                    state.LastInclude = AddInclude(state.Includes, Unquote(call.Arguments[1]).Body);
                    return;
                case nameof(EntityFrameworkQueryableExtensions.ThenInclude):
                    state.LastInclude = AddInclude(state.LastInclude ?? throw new NotSupportedException("ThenInclude without Include."), Unquote(call.Arguments[1]).Body);
                    return;
            }
        }

        else if (call.Method.DeclaringType?.Name == "RelationalQueryableExtensions" && name is "AsSplitQuery" or "AsSingleQuery")
        {
            // How the server loads includes is its own business.
            return;
        }

        throw new NotSupportedException(
            $"{name} cannot run on a Ligare server. Supported: Where, OrderBy, ThenBy, Skip, Take, Include, ThenInclude, AsNoTracking, " +
            "and First, Single, Any, Count, ToList (async). Load the data first, then continue in memory.");
    }

    /// <summary><c>e => e.A.B</c> or a filtered include <c>e => e.Items.OrderBy(i => i.Date)</c>.</summary>
    private static IncludeNode AddInclude(IncludeNode parent, Expression body)
    {
        var orderings = new List<(LambdaExpression, bool)>();
        while (body is MethodCallExpression call && (call.Method.DeclaringType == typeof(Enumerable) || call.Method.DeclaringType == typeof(Queryable)))
        {
            var descending = call.Method.Name is nameof(Enumerable.OrderByDescending) or nameof(Enumerable.ThenByDescending);
            if (call.Method.Name is not (nameof(Enumerable.OrderBy) or nameof(Enumerable.OrderByDescending) or nameof(Enumerable.ThenBy) or nameof(Enumerable.ThenByDescending)))
            {
                throw new NotSupportedException($"Filtered includes support OrderBy and ThenBy only, not {call.Method.Name}.");
            }

            orderings.Insert(0, (Unquote(call.Arguments[1]), descending));
            body = call.Arguments[0];
        }

        var members = new Stack<string>();
        while (body is MemberExpression member)
        {
            members.Push(member.Member.Name);
            body = member.Expression!;
        }

        if (body is not ParameterExpression || members.Count == 0)
        {
            throw new NotSupportedException($"Include expects navigations like e => e.Status, not '{body}'.");
        }

        var node = parent;
        while (members.TryPop(out var navigation))
        {
            node = node.Child(navigation);
        }

        node.Orderings.Clear();
        node.Orderings.AddRange(orderings);
        return node;
    }

    internal static LambdaExpression Unquote(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Quote } quote ? (LambdaExpression)quote.Operand : (LambdaExpression)expression;

    internal static T Evaluate<T>(Expression expression) => (T)Expression.Lambda(expression).Compile().DynamicInvoke()!;

    private sealed class State(bool tracking)
    {
        private readonly List<string> _filters = [];

        public Type? ElementType { get; set; }

        public List<string> OrderBy { get; } = [];

        public int? Skip { get; set; }

        public int? Top { get; set; }

        public IncludeNode Includes { get; } = new();

        public IncludeNode? LastInclude { get; set; }

        public QueryTerminal Terminal { get; set; } = QueryTerminal.List;

        public bool Tracking { get; set; } = tracking;

        public void AddFilter(LambdaExpression predicate) => _filters.Add(ODataFilterWriter.Write(predicate));

        public void EnsureNoPaging(string method)
        {
            if (Skip is not null || Top is not null)
            {
                throw new NotSupportedException($"{method} after Skip or Take cannot run on a Ligare server; filter and sort first.");
            }
        }

        public RemoteQuery Build()
        {
            var top = Terminal switch
            {
                QueryTerminal.First or QueryTerminal.FirstOrDefault or QueryTerminal.Any => Math.Min(Top ?? 1, 1),
                QueryTerminal.Single or QueryTerminal.SingleOrDefault => Math.Min(Top ?? 2, 2),
                _ => Top,
            };

            if (Terminal is QueryTerminal.Count or QueryTerminal.LongCount && (Skip is not null || Top is not null))
            {
                throw new NotSupportedException("Count after Skip or Take cannot run on a Ligare server.");
            }

            var filter = _filters.Count switch
            {
                0 => null,
                1 => _filters[0],
                _ => string.Join(" and ", _filters.Select(f => $"({f})")),
            };

            return new RemoteQuery(ElementType!, filter, [.. OrderBy], Skip, top, Includes, Terminal, Tracking);
        }
    }
}

/// <summary>Writes a predicate as an OData <c>$filter</c> expression.</summary>
internal sealed class ODataFilterWriter
{
    private readonly StringBuilder _text = new();
    private readonly Dictionary<ParameterExpression, string> _parameters = [];

    private ODataFilterWriter(ParameterExpression root)
    {
        _parameters[root] = "";
    }

    public static string Write(LambdaExpression predicate)
    {
        var writer = new ODataFilterWriter(predicate.Parameters[0]);
        writer.Visit(predicate.Body);
        return writer._text.ToString();
    }

    /// <summary>A member path of the lambda parameter, e.g. <c>Status/Name</c>.</summary>
    public static string Path(LambdaExpression selector)
    {
        var writer = new ODataFilterWriter(selector.Parameters[0]);
        writer.Visit(StripConvert(selector.Body));
        return writer._text.ToString();
    }

    private void Visit(Expression node)
    {
        if (!DependsOnParameter(node))
        {
            WriteLiteral(QueryTranslator.Evaluate<object?>(Expression.Convert(node, typeof(object))));
            return;
        }

        switch (node)
        {
            case BinaryExpression binary:
                WriteBinary(binary);
                break;
            case UnaryExpression { NodeType: ExpressionType.Not } not:
                _text.Append("not (");
                Visit(not.Operand);
                _text.Append(')');
                break;
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                Visit(convert.Operand);
                break;
            case MemberExpression { Member.Name: "HasValue" } hasValue when Nullable.GetUnderlyingType(hasValue.Expression!.Type) is not null:
                Visit(hasValue.Expression);
                _text.Append(" ne null");
                break;
            case MemberExpression { Member.Name: "Value" } value when Nullable.GetUnderlyingType(value.Expression!.Type) is not null:
                Visit(value.Expression);
                break;
            case MemberExpression member:
                WriteMember(member);
                break;
            case ParameterExpression parameter:
                _text.Append(_parameters[parameter]);
                break;
            case MethodCallExpression call:
                WriteCall(call);
                break;
            default:
                throw new NotSupportedException($"'{node}' cannot be sent to a Ligare server.");
        }
    }

    private void WriteBinary(BinaryExpression binary)
    {
        var op = binary.NodeType switch
        {
            ExpressionType.Equal => "eq",
            ExpressionType.NotEqual => "ne",
            ExpressionType.LessThan => "lt",
            ExpressionType.LessThanOrEqual => "le",
            ExpressionType.GreaterThan => "gt",
            ExpressionType.GreaterThanOrEqual => "ge",
            ExpressionType.AndAlso => "and",
            ExpressionType.OrElse => "or",
            ExpressionType.Add => "add",
            ExpressionType.Subtract => "sub",
            ExpressionType.Multiply => "mul",
            ExpressionType.Divide => "div",
            ExpressionType.Modulo => "mod",
            _ => throw new NotSupportedException($"The operator {binary.NodeType} cannot be sent to a Ligare server."),
        };

        // Enum comparisons are compiled as integer comparisons: convert the other side back to the enum.
        var enumType = EnumTypeOf(binary.Left) ?? EnumTypeOf(binary.Right);

        _text.Append('(');
        WriteOperand(binary.Left, enumType);
        _text.Append(' ').Append(op).Append(' ');
        WriteOperand(binary.Right, enumType);
        _text.Append(')');
    }

    private void WriteOperand(Expression operand, Type? enumType)
    {
        if (enumType is not null && !DependsOnParameter(operand))
        {
            var value = QueryTranslator.Evaluate<object?>(Expression.Convert(operand, typeof(object)));
            WriteLiteral(value is null ? null : Enum.ToObject(enumType, value));
            return;
        }

        Visit(operand);
    }

    private static Type? EnumTypeOf(Expression expression)
    {
        var inner = StripConvert(expression);
        var type = Nullable.GetUnderlyingType(inner.Type) ?? inner.Type;
        return type.IsEnum && DependsOnParameter(inner) ? type : null;
    }

    private void WriteMember(MemberExpression member)
    {
        if (member.Expression is null)
        {
            throw new NotSupportedException($"Static member '{member.Member.Name}' cannot be sent to a Ligare server.");
        }

        var prefixLength = _text.Length;
        Visit(member.Expression);
        if (_text.Length > prefixLength)
        {
            _text.Append('/');
        }

        _text.Append(member.Member.Name);
    }

    private void WriteCall(MethodCallExpression call)
    {
        var method = call.Method;

        if (method.DeclaringType == typeof(string))
        {
            var function = method.Name switch
            {
                nameof(string.Contains) => "contains",
                nameof(string.StartsWith) => "startswith",
                nameof(string.EndsWith) => "endswith",
                nameof(string.ToLower) or nameof(string.ToLowerInvariant) => "tolower",
                nameof(string.ToUpper) or nameof(string.ToUpperInvariant) => "toupper",
                nameof(string.Trim) => "trim",
                _ => null,
            };

            if (function is not null && call.Object is not null && call.Arguments.Count <= 1)
            {
                _text.Append(function).Append('(');
                Visit(call.Object);
                foreach (var argument in call.Arguments)
                {
                    _text.Append(',');
                    Visit(argument);
                }

                _text.Append(')');
                return;
            }
        }

        if (method.Name is nameof(Enumerable.Any) or nameof(Enumerable.All) && method.DeclaringType == typeof(Enumerable))
        {
            Visit(call.Arguments[0]);
            var alias = "x" + _parameters.Count.ToString(CultureInfo.InvariantCulture);
            _text.Append('/').Append(method.Name == nameof(Enumerable.Any) ? "any" : "all").Append('(');
            if (call.Arguments.Count == 2)
            {
                var lambda = (LambdaExpression)call.Arguments[1];
                _parameters[lambda.Parameters[0]] = alias;
                _text.Append(alias).Append(": ");
                Visit(lambda.Body);
            }

            _text.Append(')');
            return;
        }

        // values.Contains(e.Id), as Enumerable.Contains or an instance method of a collection.
        if (method.Name == nameof(Enumerable.Contains))
        {
            var (collection, item) = method.IsStatic ? (call.Arguments[0], call.Arguments[1]) : (call.Object!, call.Arguments[0]);

            // C# 14 binds array.Contains(x) to MemoryExtensions.Contains(ReadOnlySpan<T>, T): unwrap the span conversion.
            while (collection is MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } implicitConversion)
            {
                collection = implicitConversion.Arguments[0];
            }

            if (!DependsOnParameter(collection))
            {
                var values = QueryTranslator.Evaluate<IEnumerable>(Expression.Convert(collection, typeof(IEnumerable)));
                Visit(item);
                _text.Append(" in (");
                var first = true;
                foreach (var value in values)
                {
                    if (!first)
                    {
                        _text.Append(',');
                    }

                    WriteLiteral(value);
                    first = false;
                }

                _text.Append(')');
                return;
            }
        }

        throw new NotSupportedException($"The method {method.DeclaringType?.Name}.{method.Name} cannot be sent to a Ligare server.");
    }

    private void WriteLiteral(object? value)
    {
        _text.Append(value switch
        {
            null => "null",
            string s => "'" + s.Replace("'", "''") + "'",
            bool b => b ? "true" : "false",
            Guid g => g.ToString("D"),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            DateTimeOffset o => o.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
            DateTime dt => new DateTimeOffset(dt).ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
            Enum e => $"{e.GetType().FullName}'{e}'",
            float or double or decimal => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            _ => throw new NotSupportedException($"A value of type {value.GetType().Name} cannot be sent to a Ligare server; compare its properties instead."),
        });
    }

    private static Expression StripConvert(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert ? StripConvert(convert.Operand) : expression;

    private static bool DependsOnParameter(Expression expression)
    {
        var finder = new ParameterFinder();
        finder.Visit(expression);
        return finder.Found;
    }

    private sealed class ParameterFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found = true;
            return node;
        }

        protected override Expression VisitExtension(Expression node) => node;
    }
}

/// <summary>The root of a remote query: all entities of one type on the server.</summary>
internal sealed class RemoteRootExpression(Type elementType) : Expression
{
    public Type ElementType { get; } = elementType;

    public override ExpressionType NodeType => ExpressionType.Extension;

    public override Type Type { get; } = typeof(IQueryable<>).MakeGenericType(elementType);

    public override bool CanReduce => false;

    protected override Expression VisitChildren(ExpressionVisitor visitor) => this;

    public override string ToString() => $"Ligare<{ElementType.Name}>";
}

internal static class ReflectionExtensions
{
    public static MethodInfo GenericMethod(this Type type, string name) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
}
