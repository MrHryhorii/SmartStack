namespace ONNX_Runner.Models;

/// <summary>
/// Cross-Origin Resource Sharing (CORS) configuration for frontend integration.
/// </summary>
public class CorsSettings
{
    /// <summary>
    /// If true, allows API requests from anywhere (including local HTML files or any web domain).
    /// If false, the server will strictly accept requests ONLY from the domains listed in AllowedOrigins.
    /// </summary>
    public bool AllowAnyOrigin { get; set; } = true;

    /// <summary>
    /// A list of allowed trusted domains (e.g., ["https://my-frontend.com"]). 
    /// Only active if AllowAnyOrigin is false.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];
}