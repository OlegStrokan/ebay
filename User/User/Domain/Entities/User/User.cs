using System;
using System.Collections.Generic;
using DeliveryInfoEntity = Domain.Entities.DeliveryInfo.DeliveryInfo;
using Domain.Entities.Role;
using Domain.Entities.UserRestriction;

namespace Domain.Entities.User;

public enum UserStatus
{
    Active = 0,
    Restricted = 1,
    Banned = 2,
}

public enum CustomerTier
{
    Standard = 0,
    Subscriber = 1,
    Premium = 2,
}

public class UserEntity
{
    public required string Id { get; init; }
    public required string Fullname { get; set; }
    public required string Password { get; set; }
    public required string Email { get; set; }
    public required string Phone { get; set; }
    public required string CountryCode { get; set; }
    public bool IsEmailVerified { get; set; } = false;

    public CustomerTier CustomerTier { get; set; } = CustomerTier.Standard;
    public UserStatus Status { get; set; } = UserStatus.Active;

    public string? CompanyId { get; set; }

    public required DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<DeliveryInfoEntity> DeliveryInfos { get; set; } = [];
    public List<UserRoleEntity> UserRoles { get; set; } = [];
    public List<UserRestrictionEntity> Restrictions { get; set; } = [];
}