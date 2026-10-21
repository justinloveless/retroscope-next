namespace Retroscope.Core.Security;

public enum KeyPurpose {
    OidcCookie,
    JiraCredentials,
    InviteBox,
    AuditEmail,
}

internal static class KeyPurposeInfo {
    public static string Of(KeyPurpose purpose) {
        var name = purpose switch {
            KeyPurpose.OidcCookie => "oidc-cookie",
            KeyPurpose.JiraCredentials => "jira-creds",
            KeyPurpose.InviteBox => "invite-box",
            KeyPurpose.AuditEmail => "audit-email",
            _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null),
        };
        return $"retroscope/{name}/v1";
    }
}
