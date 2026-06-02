using System.Collections.Generic;
using Application.Dtos;
using Domain.Entities.User;

namespace Application.UseCases.VerifyCredentials;

public record VerifyCredentialsResponse(
    string Id,
    string Email,
    string Fullname,
    string Phone,
    string CountryCode,
    CustomerTier CustomerTier,
    UserStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsEmailVerified,
    IReadOnlyList<DeliveryInfoDto>? DeliveryInfos = null,
    IReadOnlyList<string>? Roles = null,
    string? CompanyId = null);