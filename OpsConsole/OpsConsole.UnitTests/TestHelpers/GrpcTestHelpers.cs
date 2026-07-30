using Grpc.Core;

namespace OpsConsole.UnitTests.TestHelpers;

public static class GrpcTestHelpers
{
    public static AsyncUnaryCall<T> GrpcCall<T>(T response) =>
        new(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

    public static AsyncUnaryCall<T> GrpcFail<T>(StatusCode code, string detail) =>
        new(
            Task.FromException<T>(new RpcException(new Status(code, detail))),
            Task.FromException<Metadata>(new RpcException(new Status(code, detail))),
            () => new Status(code, detail),
            () => new Metadata(),
            () => { });
}
