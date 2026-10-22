using Insurance.Application.Services;

namespace Insurance.Web.Infrastructure;

public static class PostedFormExtensions
{
    /// <summary>The posted fields as a server action received them (FormData), without the anti-forgery token.</summary>
    public static PostedForm PostedForm(this HttpRequest request)
    {
        if (!request.HasFormContentType) return new PostedForm([]);
        var fields = new List<KeyValuePair<string, string?>>();
        foreach (var (key, values) in request.Form)
        {
            if (key == "__RequestVerificationToken") continue;
            foreach (var value in values) fields.Add(new(key, value));
        }
        return new PostedForm(fields);
    }
}
