using System.Net.Mail;
using System.Text.RegularExpressions;
using ScheduleManager.Application.Errors;

namespace ScheduleManager.Application.Services;

internal static partial class Validation
{
    [GeneratedRegex("[^0-9+]", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneCharacters();

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    public static string NormalizePhone(string phone) => PhoneCharacters().Replace(phone.Trim(), string.Empty);

    public static bool IsEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320) return false;
        try { return new MailAddress(email).Address.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase); }
        catch (FormatException) { return false; }
    }

    public static void Password(string password)
    {
        var errors = new Dictionary<string, string[]>();
        if (password is null || password.Length is < 12 or > 128)
        {
            errors["newPassword"] = ["A senha deve possuir entre 12 e 128 caracteres."];
        }

        if (errors.Count > 0) throw AppException.Validation(errors);
    }

    public static (int Page, int PageSize) Page(int page, int pageSize)
    {
        if (page < 1 || pageSize is < 1 or > 100)
        {
            throw AppException.Validation(new Dictionary<string, string[]>
            {
                ["pagination"] = ["page deve ser >= 1 e pageSize deve estar entre 1 e 100."]
            });
        }
        return (page, pageSize);
    }

    public static byte[] RowVersion(string? value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value)) throw new FormatException();
            var result = Convert.FromBase64String(value);
            if (result.Length != 8) throw new FormatException();
            return result;
        }
        catch (FormatException)
        {
            throw AppException.Validation(new Dictionary<string, string[]>
            {
                ["rowVersion"] = ["rowVersion deve ser um valor Base64 válido."]
            });
        }
    }
}
