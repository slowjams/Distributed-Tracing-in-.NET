using Microsoft.Extensions.Http.Logging;
using System.Net.Http.Headers;
using System.Diagnostics;
using Microsoft.Extensions.Http;

namespace frontend;
public class StorageService
{
    private readonly HttpClient _backend;
    public StorageService(IHttpClientFactory httpClientFactory)
    {
        _backend = httpClientFactory.CreateClient("storage");
    }

    public Task<Stream> ReadAsync(string name, CancellationToken cancellationToken)
    {
        return _backend.GetStreamAsync("/memes/" + name, cancellationToken);
    }

    public async Task WriteAsync(string name, Stream fileStream, CancellationToken cancellationToken)
    {
        using var content = new StreamContent(fileStream);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        var response = await _backend.PutAsync("/memes/" + name, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}