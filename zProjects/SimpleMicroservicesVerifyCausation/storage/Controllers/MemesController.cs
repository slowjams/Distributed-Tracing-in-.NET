using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace storage.Controllers;

[ApiController]
[Route("[controller]")]
public class MemesController : ControllerBase
{
    private readonly MemeDbContext _dbContext;
    private readonly ILogger<MemesController> _logger;

    public MemesController(MemeDbContext dbContext, ILogger<MemesController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet("{name}")]
    [Produces("image/png")]
    public async Task<ActionResult> Get(string name, CancellationToken cancellationToken)
    {
        _logger.LogDebug("This is it------------------");
        _logger.LogInformation("This is it------------------");

        Meme? meme = await _dbContext.Meme.Where(m => m.Name == name).SingleOrDefaultAsync(cancellationToken);
        if (meme != null) {
            _logger.LogInformation("Returning '{meme}', {size} bytes", meme.Name, meme.Data.Length);
            return new FileStreamResult(new MemoryStream(meme.Data), "image/png");
        }

        _logger.LogWarning("Meme '{name}' not found", name);
        return new NotFoundResult();
    }

    [HttpPut("{name}")]
    [Consumes("image/png")]
    public async Task<ActionResult> Put(string name, CancellationToken cancellationToken) {
        using var stream = new MemoryStream();
        await Request.Body.CopyToAsync(stream, cancellationToken);

        _dbContext.Meme.Add(new Meme(name, stream.ToArray()));
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Uploading '{name}', {size} bytes", name, stream.Length);

        return Ok();
    }
}
