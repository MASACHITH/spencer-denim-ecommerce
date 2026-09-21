namespace SpencerDenim.Domain.Identity;

/// <summary>
/// <c>identity.Users.UserType</c> (doc 08 §3.1, D7): one Identity store holds both customers and staff.
/// Mapped to a DB <c>tinyint</c> with a CHECK constraint (doc 08 §1.4).
/// </summary>
public enum UserType : byte
{
    Customer = 1,
    Staff = 2,
}
