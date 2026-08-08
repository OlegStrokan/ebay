using Api.GrpcServices;
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
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Api.Tests.TestHelpers;

internal static class UserGrpcServiceTestFactory
{
    public static UserGrpcService Create(
        ICreateUserUseCase? createUserUseCase = null,
        IUpdateUserUseCase? updateUserUseCase = null,
        IGetUserByIdUseCase? getUserByIdUseCase = null,
        IGetUserByEmailUseCase? getUserByEmailUseCase = null,
        IVerifyCredentialsUseCase? verifyCredentialsUseCase = null,
        IDeleteUserUseCase? deleteUserUseCase = null,
        IRestrictUserUseCase? restrictUserUseCase = null,
        ILiftRestrictionUseCase? liftRestrictionUseCase = null,
        IUpdatePasswordUseCase? updatePasswordUseCase = null,
        IVerifyUserEmailUseCase? verifyUserEmailUseCase = null,
        IUpdateUserPasswordUseCase? updateUserPasswordUseCase = null,
        IAssignRoleUseCase? assignRoleUseCase = null,
        IRevokeRoleUseCase? revokeRoleUseCase = null,
        IGetUserRolesUseCase? getUserRolesUseCase = null,
        IGetAllRolesUseCase? getAllRolesUseCase = null,
        IConfiguration? configuration = null)
    {
        return new UserGrpcService(
            createUserUseCase ?? Substitute.For<ICreateUserUseCase>(),
            updateUserUseCase ?? Substitute.For<IUpdateUserUseCase>(),
            getUserByIdUseCase ?? Substitute.For<IGetUserByIdUseCase>(),
            getUserByEmailUseCase ?? Substitute.For<IGetUserByEmailUseCase>(),
            verifyCredentialsUseCase ?? Substitute.For<IVerifyCredentialsUseCase>(),
            deleteUserUseCase ?? Substitute.For<IDeleteUserUseCase>(),
            restrictUserUseCase ?? Substitute.For<IRestrictUserUseCase>(),
            liftRestrictionUseCase ?? Substitute.For<ILiftRestrictionUseCase>(),
            updatePasswordUseCase ?? Substitute.For<IUpdatePasswordUseCase>(),
            verifyUserEmailUseCase ?? Substitute.For<IVerifyUserEmailUseCase>(),
            updateUserPasswordUseCase ?? Substitute.For<IUpdateUserPasswordUseCase>(),
            assignRoleUseCase ?? Substitute.For<IAssignRoleUseCase>(),
            revokeRoleUseCase ?? Substitute.For<IRevokeRoleUseCase>(),
            getUserRolesUseCase ?? Substitute.For<IGetUserRolesUseCase>(),
            getAllRolesUseCase ?? Substitute.For<IGetAllRolesUseCase>(),
            // Empty by default: leaves Testing:AutoVerifyEmail false and InternalServices:ApiKey
            // unset, which disables the x-internal-api-key check on GetUserByEmail.
            configuration ?? new ConfigurationBuilder().AddInMemoryCollection().Build());
    }
}
