using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Net;

namespace frontend.Pages;

public class Meme : PageModel
{
    private readonly StorageService _storage;
    private readonly ILogger<Meme> _logger;
    public Meme(StorageService storage, ILogger<Meme> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public string? ImageBase64 { get; set; }

    [BindProperty]
    public CancellationToken CancellationToken { get; set; }

    public async Task<IActionResult> OnGet()
    {
        Name = Request.Query["name"]!;
        _logger.LogDebug("This is from frontend------------------");

        try 
        {
            using var stream = await _storage.ReadAsync(Name, CancellationToken);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, CancellationToken);
            copy.Position = 0;
            ImageBase64 = Convert.ToBase64String(copy.ToArray());
            return new PageResult();
        } 
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundResult();
        }
    }
}
