using System.Text;

namespace NetLoop.Socks;

public static class Socks5Credentials
{
    public static void Validate(string? username, string? password)
    {
        if ((username is null) != (password is null))
        {
            throw new ArgumentException(
                "SOCKS5 RFC1929 username and password must either both be set or both be absent.");
        }

        if (username is null)
            return;

        var usernameLength = Encoding.UTF8.GetByteCount(username);
        var passwordLength = Encoding.UTF8.GetByteCount(password!);
        if (usernameLength is 0 or > 255
            || passwordLength is 0 or > 255)
        {
            throw new ArgumentException(
                "SOCKS5 RFC1929 username and password must each be 1..255 UTF-8 bytes.");
        }
    }
}
