using CSweet.Agent.SDK;

namespace CSweet.Agent.Platform.YouTube;

public sealed record PublicationMedia(ConversationAttachmentReference Source, Guid AssetId, string FileName,
    string ContentType, long SizeBytes, string Sha256, Guid SenderOrganizationUserId)
{
    public static IReadOnlyList<PublicationMedia> Capture(CommunicationMessage message)
    {
        if (message.Id == Guid.Empty || message.ChatId == Guid.Empty || message.SenderOrganizationUserId is null ||
            message.SenderOrganizationUserId == Guid.Empty || message.Attachments is null || message.Attachments.Count > 8)
            throw new InvalidOperationException("The original attachment message could not be verified.");
        var result = new List<PublicationMedia>();
        foreach (var attachment in message.Attachments.Where(x => x.ContentType is "video/mp4" or "video/webm"))
        {
            if (attachment.Id == Guid.Empty || attachment.MessageId != message.Id ||
                attachment.MediaAssetId is not { } assetId || assetId == Guid.Empty ||
                string.IsNullOrWhiteSpace(attachment.FileName) || attachment.FileName.Length > 255 ||
                attachment.FileName.Any(char.IsControl) || attachment.SizeBytes is <= 0 or > 256L * 1024 * 1024 * 1024 ||
                string.IsNullOrWhiteSpace(attachment.Sha256) || attachment.Sha256.Length != 64 || !attachment.Sha256.All(Uri.IsHexDigit) ||
                result.Any(x => x.AssetId == assetId || x.Source.AttachmentId == attachment.Id))
                throw new InvalidOperationException("The video attachment is incomplete or ambiguous. Attach the original video again.");
            result.Add(new(new(message.ChatId, message.Id, attachment.Id), assetId, attachment.FileName,
                attachment.ContentType, attachment.SizeBytes, attachment.Sha256, message.SenderOrganizationUserId.Value));
        }
        return result;
    }

    public static PublicationMedia Select(IReadOnlyList<PublicationMedia> media, string? fileName = null)
    {
        var matches = fileName is null ? media : media.Where(x => string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Count != 1)
            throw new InvalidOperationException("Please choose one attached video by its file name.");
        return matches[0];
    }

    public static void RequireUnchanged(IReadOnlyList<PublicationMedia> saved, CommunicationMessage current)
    {
        var fresh = Capture(current);
        if (saved.Count != fresh.Count || saved.Any(x => !fresh.Contains(x)))
            throw new InvalidOperationException("The video attachment changed after this request was saved. Review the original request before continuing.");
    }
}
