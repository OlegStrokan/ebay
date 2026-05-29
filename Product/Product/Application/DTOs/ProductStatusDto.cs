namespace Application.DTOs;

public sealed record ProductStatusDto(Guid ProductId, string Status, string? ReviewNotes);
