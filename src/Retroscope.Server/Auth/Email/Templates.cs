using System.Text.RegularExpressions;

namespace Retroscope.Server.Auth.Email;

/// <summary>A rendered mail before it has a recipient.</summary>
public sealed record MailContent(string Subject, string Body) {
    public OutboxMail To(string address) => new(address, Subject, Body);
}

/// <summary>
/// Every mail the server sends (§e): fixed plain-text bodies, no HTML. Links carry the token in the fragment, so it
/// never reaches a server log or a Referer. Nothing a stranger typed reaches a mail except an invite's team name,
/// which is quoted and stripped of line breaks and anything a mail client would turn into a link (S8).
/// </summary>
public static partial class Templates {
    public const int MaxTeamNameLength = 100;

    public static MailContent VerifyEmail(string link) => new(
        "Confirm your email for Retroscope",
        $"""
        Welcome to Retroscope.

        Confirm that this is your email address by opening this link while signed in:

        {link}

        The link expires in 24 hours.

        If you didn't create this account, ignore this email.
        """);

    public static MailContent AlreadyRegistered(string origin) => new(
        "You already have a Retroscope account",
        $"""
        Someone tried to create a Retroscope account with this email address, but it already has one.

        If it was you, sign in at {origin}/login. If you forgot your password, choose "Forgot password" there.

        If it wasn't you, ignore this email. Nothing has changed on your account.
        """);

    public static MailContent MagicLink(string link) => new(
        "Your Retroscope sign-in link",
        $"""
        Open this link to sign in to Retroscope:

        {link}

        The link works once and expires in 15 minutes.

        If you didn't ask to sign in, ignore this email.
        """);

    public static MailContent LoginCapped(string link) => new(
        "Too many failed sign-ins on your Retroscope account",
        $"""
        Someone entered the wrong password for your Retroscope account many times in the last hour, so password
        sign-in is paused for a while.

        You can still sign in with this one-time link:

        {link}

        The link works once and expires in 15 minutes. If the failed attempts weren't you, consider changing your
        password after you sign in.
        """);

    public static MailContent PasswordReset(string link) => new(
        "Reset your Retroscope password",
        $"""
        Open this link to choose a new password for your Retroscope account:

        {link}

        The link works once and expires in 30 minutes. Setting a new password signs you out everywhere.

        If you didn't ask to reset your password, ignore this email.
        """);

    public static MailContent TeamInvite(string teamName, string link) => new(
        "You're invited to a team on Retroscope",
        $"""
        You have been invited to join the team "{InertTeamName(teamName)}" on Retroscope.

        Open this link to accept:

        {link}

        If you weren't expecting this invitation, ignore this email.
        """);

    /// <summary>The team name as an invite quotes it: one line, no URLs or host names, no quotes, at most 100 characters.</summary>
    public static string InertTeamName(string teamName) {
        var words = teamName
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string([.. word.Where(c => !char.IsControl(c) && c is not '"')]))
            .Where(word => word.Length > 0 && !LinkLike().IsMatch(word));
        var name = string.Join(' ', words);
        return name.Length > MaxTeamNameLength ? name[..MaxTeamNameLength] : name;
    }

    // A scheme, "www.", an "@", or a dotted name ending in letters: anything a mail client might make clickable.
    [GeneratedRegex(@"[a-z][a-z0-9+.-]*:/|mailto:|www\.|@|\.[\p{L}]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkLike();
}
