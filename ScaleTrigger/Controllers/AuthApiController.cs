using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ScaleTrigger.Auth;

namespace ScaleTrigger.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthApiController : ControllerBase
    {
        private readonly IConfiguration configuration;

        public AuthApiController(IConfiguration configuration)
        {
            this.configuration = configuration;
        }

        public class LoginRequest
        {
            public string Username { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
        }

        /// <summary>Lets load-test scripts detect Auth:Enabled without a side-effecting probe vote.</summary>
        [HttpGet("status")]
        [AllowAnonymous]
        public ActionResult<object> Status()
        {
            bool authRequired = bool.TryParse(configuration["Auth:Enabled"], out bool enabled) && enabled;
            return Ok(new { authRequired });
        }

        [HttpPost("login")]
        [EnableRateLimiting("login")]
        public ActionResult<string> Login([FromBody] LoginRequest request)
        {
            string adminUsername = configuration["AdminUser:Username"] ?? string.Empty;
            string adminPassword = configuration["AdminUser:Password"] ?? string.Empty;

            bool usernameMatches = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(request.Username), Encoding.UTF8.GetBytes(adminUsername));
            bool passwordMatches = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(request.Password), Encoding.UTF8.GetBytes(adminPassword));

            if (!usernameMatches || !passwordMatches)
            {
                return Unauthorized("Invalid username or password.");
            }

            string? jwtKey = configuration["Jwt:Key"];
            string? jwtIssuer = configuration["Jwt:Issuer"];
            string? jwtAudience = configuration["Jwt:Audience"];

            if (string.IsNullOrEmpty(jwtKey) || string.IsNullOrEmpty(jwtIssuer) || string.IsNullOrEmpty(jwtAudience))
            {
                return StatusCode(StatusCodes.Status500InternalServerError,
                    "Jwt:Key, Jwt:Issuer, and Jwt:Audience must all be configured.");
            }

            if (!int.TryParse(configuration["Jwt:ExpirationMinutes"], out int expirationMinutes))
            {
                expirationMinutes = 60;
            }

            string token = JwtTokenProvider.CreateToken(
                key: jwtKey,
                issuer: jwtIssuer,
                audience: jwtAudience,
                expirationMinutes: expirationMinutes,
                username: request.Username);

            return Ok(new { token });
        }
    }
}
