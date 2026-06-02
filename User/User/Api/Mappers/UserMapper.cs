using Application.Dtos;
using Domain.Entities.DeliveryInfo;
using Domain.Entities.User;
using Protos.User;
using RestrictUserResponse = Application.UseCases.RestrictUser.RestrictUserResponse;
using CreateUserResponse = Application.UseCases.CreateUser.CreateUserResponse;
using CreateUserResponseProto = Protos.User.CreateUserResponse;
using GetUserByEmailResponse = Application.UseCases.GetUserByEmail.GetUserByEmailResponse;
using GetUserByIdResponse = Application.UseCases.GetUserById.GetUserByIdResponse;
using GetUserByEmailResponseProto = Protos.User.GetUserByEmailResponse;
using VerifyCredentialsResponse = Application.UseCases.VerifyCredentials.VerifyCredentialsResponse;
using VerifyCredentialsResponseProto = Protos.User.VerifyCredentialsResponse;
using UpdateUserResponse = Application.UseCases.UpdateUser.UpdateUserResponse;
using RestrictUserResponseProto = Protos.User.RestrictUserResponse;
using GetUserByIdResponseProto = Protos.User.GetUserByIdResponse;
using UpdateUserResponseProto = Protos.User.UpdateUserResponse;


namespace Api.Mappers;

public static class UserMapper
{
    public static UserStatusProto ToProto(this UserStatus status)
    {
        return status switch
        {
            UserStatus.Active => UserStatusProto.Active,
            UserStatus.Restricted => UserStatusProto.Restricted,
            UserStatus.Banned => UserStatusProto.Banned,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };
    }

    public static UserStatus ToEntity(this UserStatusProto status)
    {
        return status switch
        {
            UserStatusProto.Active => UserStatus.Active,
            UserStatusProto.Restricted => UserStatus.Restricted,
            UserStatusProto.Banned => UserStatus.Banned,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };
    }

    public static CustomerTierProto ToProto(this CustomerTier tier)
    {
        return tier switch
        {
            CustomerTier.Standard => CustomerTierProto.Standard,
            CustomerTier.Subscriber => CustomerTierProto.Subscriber,
            CustomerTier.Premium => CustomerTierProto.Premium,
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null)
        };
    }

    public static CustomerTier ToEntity(this CustomerTierProto tier)
    {
        return tier switch
        {
            CustomerTierProto.Standard => CustomerTier.Standard,
            CustomerTierProto.Subscriber => CustomerTier.Subscriber,
            CustomerTierProto.Premium => CustomerTier.Premium,
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null)
        };
    }

    public static DeliveryInfoProto ToProto(this DeliveryInfoDto dto)
    {
        return new DeliveryInfoProto
        {
            Id = dto.Id,
            Street = dto.Street,
            City = dto.City,
            PostalCode = dto.PostalCode,
            CountryDestination = dto.CountryDestination,
        };
    }

    public static DeliveryInfoProto ToProto(this DeliveryInfo entity)
    {
        return new DeliveryInfoProto
        {
            Id = entity.Id,
            Street = entity.Street,
            City = entity.City,
            PostalCode = entity.PostalCode,
            CountryDestination = entity.CountryDestination,
        };
    }

    public static CreateUserResponseProto ToProto(this CreateUserResponse response)
    {
        return new CreateUserResponseProto { Data = MapToUserProto(response) };
    }

    public static CreateUserResponseProto ToProto(this CreateUserResponse response, string phone)
    {
        var proto = MapToUserProto(response);
        proto.Phone = phone ?? "";
        return new CreateUserResponseProto { Data = proto };
    }

    public static UserProto ToUserProto(this CreateUserResponse response)
    {
        return MapToUserProto(response);
    }

    public static GetUserByEmailResponseProto ToProto(this GetUserByEmailResponse? response)
    {
        if (response == null) return new GetUserByEmailResponseProto();

        return new GetUserByEmailResponseProto { Data = MapToUserProto(response) };
    }

    public static VerifyCredentialsResponseProto ToProto(this VerifyCredentialsResponse? response)
    {
        if (response == null) return new VerifyCredentialsResponseProto();

        return new VerifyCredentialsResponseProto
        {
            Data = MapToUserProto(response),
            IsValid = true,
        };
    }

    public static GetUserByIdResponseProto ToProto(this GetUserByIdResponse? response)
    {
        if (response == null) return new GetUserByIdResponseProto { Data = null };

        return new GetUserByIdResponseProto { Data = MapToUserProto(response) };
    }

    public static UpdateUserResponseProto ToProto(this UpdateUserResponse response)
    {
        return new UpdateUserResponseProto { Data = MapToUserProto(response) };
    }

    public static RestrictUserResponseProto ToProto(this RestrictUserResponse response)
    {
        return new RestrictUserResponseProto { Data = MapToUserProto(response) };
    }

    public static UserProto ToProto(this UserEntity entity)
    {
        var proto = new UserProto
        {
            Id = entity.Id.ToString(),
            FullName = entity.Fullname,
            Email = entity.Email,
            Phone = entity.Phone ?? "",
            Status = entity.Status.ToProto(),
            CreatedAt = new DateTimeOffset(entity.CreatedAt).ToUnixTimeSeconds(),
            UpdatedAt = new DateTimeOffset(entity.UpdatedAt).ToUnixTimeSeconds(),
            CountryCode = entity.CountryCode,
            CustomerTier = entity.CustomerTier.ToProto(),
            IsEmailVerified = entity.IsEmailVerified,
        };
        if (entity.CompanyId is not null)
            proto.CompanyId = entity.CompanyId;
        proto.DeliveryInfo.AddRange(entity.DeliveryInfos.Select(d => d.ToProto()));
        proto.Roles.AddRange(entity.UserRoles.Select(ur => ur.Role.Name));
        return proto;
    }

    public static CreateUserResponseProto ToCreateUserResponseProto(this UserEntity entity)
    {
        return new CreateUserResponseProto { Data = entity.ToProto() };
    }

    // ------------------------------------------------------------------
    // private helpers
    // ------------------------------------------------------------------

    private static UserProto MapToUserProto(CreateUserResponse r)
    {
        var proto = new UserProto
        {
            Id = r.Id,
            FullName = r.Fullname,
            Email = r.Email,
            Phone = r.Phone,
            Status = r.Status.ToProto(),
            CreatedAt = new DateTimeOffset(r.CreatedAt).ToUnixTimeSeconds(),
            UpdatedAt = new DateTimeOffset(r.UpdatedAt).ToUnixTimeSeconds(),
            CountryCode = r.CountryCode,
            CustomerTier = r.CustomerTier.ToProto(),
            IsEmailVerified = r.IsEmailVerified,
        };
        if (r.DeliveryInfos != null)
            proto.DeliveryInfo.AddRange(r.DeliveryInfos.Select(d => d.ToProto()));
        if (r.Roles != null)
            proto.Roles.AddRange(r.Roles);
        return proto;
    }

    private static UserProto MapToUserProto(GetUserByEmailResponse r)
    {
        var proto = new UserProto
        {
            Id = r.Id,
            FullName = r.Fullname,
            Email = r.Email,
            Phone = r.Phone,
            Status = r.Status.ToProto(),
            CreatedAt = new DateTimeOffset(r.CreatedAt).ToUnixTimeSeconds(),
            UpdatedAt = new DateTimeOffset(r.UpdatedAt).ToUnixTimeSeconds(),
            CountryCode = r.CountryCode,
            CustomerTier = r.CustomerTier.ToProto(),
            IsEmailVerified = r.IsEmailVerified,
        };
        if (r.DeliveryInfos != null)
            proto.DeliveryInfo.AddRange(r.DeliveryInfos.Select(d => d.ToProto()));
        if (r.Roles != null)
            proto.Roles.AddRange(r.Roles);
        return proto;
    }

    private static UserProto MapToUserProto(VerifyCredentialsResponse r)
    {
        var proto = new UserProto
        {
            Id = r.Id,
            FullName = r.Fullname,
            Email = r.Email,
            Phone = r.Phone,
            Status = r.Status.ToProto(),
            CreatedAt = new DateTimeOffset(r.CreatedAt).ToUnixTimeSeconds(),
            UpdatedAt = new DateTimeOffset(r.UpdatedAt).ToUnixTimeSeconds(),
            CountryCode = r.CountryCode,
            CustomerTier = r.CustomerTier.ToProto(),
            IsEmailVerified = r.IsEmailVerified,
        };
        if (r.CompanyId is not null)
            proto.CompanyId = r.CompanyId;
        if (r.DeliveryInfos != null)
            proto.DeliveryInfo.AddRange(r.DeliveryInfos.Select(d => d.ToProto()));
        if (r.Roles != null)
            proto.Roles.AddRange(r.Roles);
        return proto;
    }

    private static UserProto MapToUserProto(GetUserByIdResponse r)
    {
        var proto = new UserProto
        {
            Id = r.Id,
            FullName = r.Fullname,
            Email = r.Email,
            Phone = r.Phone,
            Status = r.Status.ToProto(),
            CreatedAt = new DateTimeOffset(r.CreatedAt).ToUnixTimeSeconds(),
            UpdatedAt = new DateTimeOffset(r.UpdatedAt).ToUnixTimeSeconds(),
            CountryCode = r.CountryCode,
            CustomerTier = r.CustomerTier.ToProto(),
            IsEmailVerified = r.IsEmailVerified,
        };
        if (r.DeliveryInfos != null)
            proto.DeliveryInfo.AddRange(r.DeliveryInfos.Select(d => d.ToProto()));
        if (r.Roles != null)
            proto.Roles.AddRange(r.Roles);
        return proto;
    }

    private static UserProto MapToUserProto(UpdateUserResponse r)
    {
        var proto = new UserProto
        {
            Id = r.Id,
            FullName = r.Fullname,
            Email = r.Email,
            Phone = r.Phone,
            Status = r.Status.ToProto(),
            CreatedAt = new DateTimeOffset(r.CreatedAt).ToUnixTimeSeconds(),
            UpdatedAt = new DateTimeOffset(r.UpdatedAt).ToUnixTimeSeconds(),
            CountryCode = r.CountryCode,
            CustomerTier = r.CustomerTier.ToProto(),
            IsEmailVerified = r.IsEmailVerified,
        };
        if (r.CompanyId is not null)
            proto.CompanyId = r.CompanyId;
        if (r.DeliveryInfos != null)
            proto.DeliveryInfo.AddRange(r.DeliveryInfos.Select(d => d.ToProto()));
        if (r.Roles != null)
            proto.Roles.AddRange(r.Roles);
        return proto;
    }

    private static UserProto MapToUserProto(RestrictUserResponse r)
    {
        var proto = new UserProto
        {
            Id = r.Id,
            FullName = r.Fullname,
            Email = r.Email,
            Phone = r.Phone,
            Status = r.Status.ToProto(),
            CreatedAt = new DateTimeOffset(r.CreatedAt).ToUnixTimeSeconds(),
            UpdatedAt = new DateTimeOffset(r.UpdatedAt).ToUnixTimeSeconds(),
            CountryCode = r.CountryCode,
            CustomerTier = r.CustomerTier.ToProto(),
            IsEmailVerified = r.IsEmailVerified,
        };
        if (r.DeliveryInfos != null)
            proto.DeliveryInfo.AddRange(r.DeliveryInfos.Select(d => d.ToProto()));
        if (r.Roles != null)
            proto.Roles.AddRange(r.Roles);
        return proto;
    }
}
