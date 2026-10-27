namespace YuWangCloudSync.Core;

public static class AccountNameDisplay
{
    public static string ForDisplay(string username)
    {
        if (username.Length != 11 || username[0] != '1'
            || username.Any(character => character is < '0' or > '9'))
            return username;

        return string.Concat(username[..3], "****", username[7..]);
    }
}
