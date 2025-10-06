// Models/PlaylistsDtos.cs
namespace PlexProxy.Models;

public sealed record PlaylistSummaryDto(
    string Id,
    string Title,
    string PlaylistType,
    int? LeafCount,
    int? DurationSec
);

public sealed record PlaylistsResponseDto(
    IEnumerable<PlaylistSummaryDto> Playlists,
    int Count
);