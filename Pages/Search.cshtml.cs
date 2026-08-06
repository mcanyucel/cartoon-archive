using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CartoonArchive.Pages;

public class SearchModel : PageModel
{
    [FromQuery(Name = "q")]
    public string? Query { get; set; }

    public void OnGet()
    {
    }
}
