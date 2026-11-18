using Ligare.Remote;
using Microsoft.Extensions.DependencyInjection;

namespace Ligare;

public static class RemoteClientBuilderExtensions
{
    internal const string HttpClientName = "Ligare";

    /// <summary>
    /// Remote mode: queries and saves go to the Ligare server at <paramref name="baseAddress"/> (the
    /// path given to <c>MapLigare</c>, e.g. <c>https://example.com/data/</c>).
    /// </summary>
    public static LigareClientBuilder UseServer(this LigareClientBuilder builder, Uri baseAddress, Action<HttpClient>? configure = null)
    {
        var withSlash = baseAddress.AbsoluteUri.EndsWith('/') ? baseAddress : new Uri(baseAddress.AbsoluteUri + "/");
        builder.Services.AddHttpClient(HttpClientName, http =>
        {
            http.BaseAddress = withSlash;
            configure?.Invoke(http);
        });

        return builder.UseServer(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName));
    }

    /// <summary>Remote mode with an <see cref="HttpClient"/> you create, whose base address is the server path.</summary>
    public static LigareClientBuilder UseServer(this LigareClientBuilder builder, Func<IServiceProvider, HttpClient> createClient)
    {
        builder.Services.AddSingleton<RemoteModelSource>();
        builder.Services.AddScoped<ILigareContextFactory>(sp => new RemoteLigareContextFactory(sp, createClient));
        return builder;
    }

    private sealed class RemoteLigareContextFactory(IServiceProvider services, Func<IServiceProvider, HttpClient> createClient) : ILigareContextFactory
    {
        public ILigareContext CreateContext() => new RemoteLigareContext(createClient(services), services.GetRequiredService<RemoteModelSource>());
    }
}
