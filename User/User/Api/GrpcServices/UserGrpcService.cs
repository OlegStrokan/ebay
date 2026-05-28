using Api.Mappers;
using Application.UseCases.AssignRole;
using Application.UseCases.RestrictUser;
using Application.UseCases.LiftRestriction;
using Application.UseCases.CreateUser;
using Application.UseCases.DeleteUser;
using Application.UseCases.GetAllRoles;
using Application.UseCases.GetUserByEmail;
using Application.UseCases.GetUserById;
using Application.UseCases.GetUserRoles;
using Application.UseCases.RevokeRole;
using Application.UseCases.UpdatePassword;
using Application.UseCases.UpdateUserPassword;
using Application.UseCases.UpdateUser;
using Application.UseCases.VerifyCredentials;
using Application.UseCases.VerifyUserEmail;
using Grpc.Core;
using Protos.Role;
using Protos.User;
using AssignRoleResponseProto   = Protos.Role.AssignRoleResponse;
using RevokeRoleResponseProto   = Protos.Role.RevokeRoleResponse;
using GetUserRolesResponseProto = Protos.Role.GetUserRolesResponse;
using GetAllRolesResponseProto  = Protos.Role.GetAllRolesResponse;
using RestrictUserResponseProto = Protos.User.RestrictUserResponse;
using LiftRestrictionResponseProto = Protos.User.LiftRestrictionResponse;
using CreateUserResponseProto = Protos.User.CreateUserResponse;
using GetUserByEmailResponseProto = Protos.User.GetUserByEmailResponse;
using GetUserByIdResponseProto = Protos.User.GetUserByIdResponse;
using UpdateUserPasswordResponseProto = Protos.User.UpdateUserPasswordResponse;
using UpdateUserResponseProto = Protos.User.UpdateUserResponse;
using VerifyCredentialsResponseProto = Protos.User.VerifyCredentialsResponse;
using VerifyUserEmailResponseProto = Protos.User.VerifyUserEmailResponse;

namespace Api.GrpcServices;

public class UserGrpcService(
    ICreateUserUseCase createUserUseCase,
    IUpdateUserUseCase updateUserUseCase,
    IGetUserByIdUseCase getUserByIdUseCase,
    IGetUserByEmailUseCase getUserByEmailUseCase,
    IVerifyCredentialsUseCase verifyCredentialsUseCase,
    IDeleteUserUseCase deleteUserUseCase,
    IRestrictUserUseCase restrictUserUseCase,
    ILiftRestrictionUseCase liftRestrictionUseCase,
    IUpdatePasswordUseCase updatePasswordUseCase,
    IVerifyUserEmailUseCase verifyUserEmailUseCase,
    IUpdateUserPasswordUseCase updateUserPasswordUseCase,
    IAssignRoleUseCase assignRoleUseCase,
    IRevokeRoleUseCase revokeRoleUseCase,
    IGetUserRolesUseCase getUserRolesUseCase,
    IGetAllRolesUseCase getAllRolesUseCase,
    IConfiguration configuration) : UserServiceProto.UserServiceProtoBase
{
    public override async Task<CreateUserResponseProto> CreateUser(CreateUserRequest request, ServerCallContext context)
    {
        try
        {
            var autoVerify = configuration.GetValue<bool>("Testing:AutoVerifyEmail");
            var user = await createUserUseCase.ExecuteAsync(new CreateUserCommand(
                request.Email,
                request.Password,
                request.FullName,
                request.Phone,
                request.CountryCode,
                request.CustomerTier.ToEntity(),
                AutoVerifyEmail: autoVerify));

            return user.ToProto();
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, ex.Message));
        }
    }

    public override async Task<UpdateUserResponseProto> UpdateUser(UpdateUserRequest request, ServerCallContext context)
    {
        try
        {
            var result = await updateUserUseCase.ExecuteAsync(new UpdateUserCommand(
                request.Id,
                request.Email,
                request.FullName,
                request.Phone,
                request.CountryCode,
                request.CustomerTier.ToEntity()));

            return result.ToProto();
        }
        catch (KeyNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, ex.Message));
        }
    }

    public override async Task<GetUserByIdResponseProto> GetUserById(GetUserByIdRequest request, ServerCallContext context)
    {
        var result = await getUserByIdUseCase.ExecuteAsync(request.Id);
        if (result == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound,
                $"User with ID  {request.Id} not found"));
        }

        return result.ToProto();
    }

    public override async Task<DeleteUserResponse> DeleteUser(DeleteUserRequest request, ServerCallContext context)
    {
        await deleteUserUseCase.ExecuteAsync(request.Id);
        return new DeleteUserResponse();
    }

    public override async Task<RestrictUserResponseProto> RestrictUser(RestrictUserRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TargetUserId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "target_user_id is required"));
        if (string.IsNullOrWhiteSpace(request.ActorUserId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "actor_user_id is required"));
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "reason is required"));

        var type = request.Type == RestrictionTypeProto.RestrictionTypeBanned
            ? Domain.Entities.UserRestriction.RestrictionType.Banned
            : Domain.Entities.UserRestriction.RestrictionType.Restricted;

        DateTime? expiresAt = request.HasExpiresAt
            ? DateTimeOffset.FromUnixTimeSeconds(request.ExpiresAt).UtcDateTime
            : null;

        try
        {
            var result = await restrictUserUseCase.ExecuteAsync(
                new RestrictUserCommand(request.TargetUserId, request.ActorUserId, type, request.Reason, expiresAt));
            return result.ToProto();
        }
        catch (KeyNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    public override async Task<LiftRestrictionResponseProto> LiftRestriction(LiftRestrictionRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TargetUserId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "target_user_id is required"));
        if (string.IsNullOrWhiteSpace(request.ActorUserId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "actor_user_id is required"));

        try
        {
            var result = await liftRestrictionUseCase.ExecuteAsync(
                new LiftRestrictionCommand(request.TargetUserId, request.ActorUserId));
            return new LiftRestrictionResponseProto { Success = result.Success };
        }
        catch (KeyNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    public override async Task<UpdatePasswordResponse> UpdatePassword(UpdatePasswordRequest request, ServerCallContext context)
    {
        try
        {
            await updatePasswordUseCase.ExecuteAsync(new UpdatePasswordCommand(
                request.Id,
                request.CurrentPassword,
                request.NewPassword));

            return new UpdatePasswordResponse();
        }
        catch (KeyNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    public override async Task<GetUserByEmailResponseProto> GetUserByEmail(
        GetUserByEmailRequest request,
        ServerCallContext context)
    {
        try
        {
            var user = await getUserByEmailUseCase.ExecuteAsync(request.Email);
            return user.ToProto();
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<VerifyCredentialsResponseProto> VerifyCredentials(
        VerifyCredentialsRequest request,
        ServerCallContext context)
    {
        try
        {
            var user = await verifyCredentialsUseCase.ExecuteAsync(request.Email, request.Password);
            return user.ToProto();
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<VerifyUserEmailResponseProto> VerifyUserEmail(
        VerifyUserEmailRequest request,
        ServerCallContext context)
    {
        try
        {
            var success = await verifyUserEmailUseCase.ExecuteAsync(request.UserId);
            return new VerifyUserEmailResponseProto { Success = success };
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<UpdateUserPasswordResponseProto> UpdateUserPassword(
        UpdateUserPasswordRequest request,
        ServerCallContext context)
    {
        try
        {
            var result = await updateUserPasswordUseCase.ExecuteAsync(
                new UpdateUserPasswordCommand(request.UserId, request.NewPassword));

            return new UpdateUserPasswordResponseProto
            {
                Success = result.Success,
                Message = result.Message,
            };
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<AssignRoleResponseProto> AssignRole(AssignRoleRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "user_id is required"));
        if (string.IsNullOrWhiteSpace(request.RoleName))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "role_name is required"));
        if (string.IsNullOrWhiteSpace(request.AssignedBy))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "assigned_by is required"));

        try
        {
            var result = await assignRoleUseCase.ExecuteAsync(
                new AssignRoleCommand(request.UserId, request.RoleName, request.AssignedBy));
            return new AssignRoleResponseProto { Success = result.Success };
        }
        catch (KeyNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, ex.Message));
        }
    }

    public override async Task<RevokeRoleResponseProto> RevokeRole(RevokeRoleRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "user_id is required"));
        if (string.IsNullOrWhiteSpace(request.RoleName))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "role_name is required"));

        try
        {
            var result = await revokeRoleUseCase.ExecuteAsync(
                new RevokeRoleCommand(request.UserId, request.RoleName));
            return new RevokeRoleResponseProto { Success = result.Success };
        }
        catch (KeyNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    public override async Task<GetUserRolesResponseProto> GetUserRoles(GetUserRolesRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "user_id is required"));

        try
        {
            var result = await getUserRolesUseCase.ExecuteAsync(new GetUserRolesQuery(request.UserId));
            var response = new GetUserRolesResponseProto();
            response.Roles.AddRange(result.RoleNames.Select(name => new RoleProto { Name = name }));
            return response;
        }
        catch (KeyNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
    }

    public override async Task<GetAllRolesResponseProto> GetAllRoles(GetAllRolesRequest request, ServerCallContext context)
    {
        var result = await getAllRolesUseCase.ExecuteAsync();
        var response = new GetAllRolesResponseProto();
        response.Roles.AddRange(result.RoleNames.Select(name => new RoleProto { Name = name }));
        return response;
    }
}
