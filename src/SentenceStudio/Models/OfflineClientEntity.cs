using System;
using System.ComponentModel.DataAnnotations;

namespace SentenceStudio.Models;

/// <summary>
/// An abstract class for working with offline entities.
/// </summary>
public abstract class OfflineClientEntity
{
    /// <summary>
    /// The Id is used for cloud synchronization.
    /// </summary>
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? Version { get; set; }
    public bool Deleted { get; set; }
}