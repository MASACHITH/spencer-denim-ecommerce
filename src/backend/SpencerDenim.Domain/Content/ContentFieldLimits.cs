namespace SpencerDenim.Domain.Content;

/// <summary>
/// The column widths of doc 08 §3.7 for the admin-editable homepage and announcement rows, in one place,
/// so the ADM-17 validators and the EF configurations cannot disagree about them. A value one character
/// over is a named field error from the API, never a truncation error from SQL Server.
/// </summary>
public static class ContentFieldLimits
{
    /// <summary><c>HomeSections.Title</c> and <c>HomeSectionItems.Title</c>: <c>nvarchar(150)</c>.</summary>
    public const int TitleMaxLength = 150;

    /// <summary><c>HomeSections.Subtitle</c> and <c>HomeSectionItems.Subtitle</c>: <c>nvarchar(300)</c>.</summary>
    public const int SubtitleMaxLength = 300;

    /// <summary><c>HomeSectionItems.Body</c>: <c>nvarchar(1000)</c>.</summary>
    public const int BodyMaxLength = 1000;

    /// <summary><c>HomeSectionItems.ButtonLabel</c>: <c>nvarchar(40)</c>. Also the section's <c>linkLabel</c>.</summary>
    public const int ButtonLabelMaxLength = 40;

    /// <summary><c>HomeSectionItems.LinkUrl</c> and <c>AnnouncementMessages.LinkUrl</c>: <c>nvarchar(500)</c>.</summary>
    public const int LinkUrlMaxLength = 500;

    /// <summary><c>HomeSectionItems.IconKey</c>: <c>varchar(40)</c>.</summary>
    public const int IconKeyMaxLength = 40;

    /// <summary><c>AnnouncementMessages.Message</c>: <c>nvarchar(200)</c> (doc 11 P13 step 9: "up to 200 characters").</summary>
    public const int AnnouncementMaxLength = 200;

    /// <summary>
    /// The position a staff member may give a block, an item or a message. <c>smallint</c> holds far more;
    /// three digits is what a person types into a "position" box, and it keeps a typo from becoming
    /// 32,767.
    /// </summary>
    public const short MaxSortOrder = 999;
}
