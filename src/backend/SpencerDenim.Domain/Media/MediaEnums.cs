namespace SpencerDenim.Domain.Media;

/// <summary><c>media.MediaAssets.MediaType</c> (doc 08 §3.3).</summary>
public enum MediaAssetType : byte
{
    Image = 1,
    Video = 2,
    Document = 3,
}

/// <summary>
/// <c>media.MediaAssets.Visibility</c> (doc 08 §3.3). Private assets (bank slips) live on a volume
/// that the reverse proxy does not mount and are streamed only by an authorised API.
/// </summary>
public enum MediaVisibility : byte
{
    Public = 1,
    Private = 2,
}

/// <summary><c>media.MediaAssets.Status</c> (doc 08 §3.3).</summary>
public enum MediaAssetStatus : byte
{
    Processing = 0,
    Ready = 1,
    Failed = 2,
}
