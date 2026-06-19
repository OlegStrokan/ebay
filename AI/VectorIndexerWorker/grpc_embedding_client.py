import uuid

import grpc
import grpc.aio
import structlog

import embedding_pb2
import embedding_pb2_grpc

log = structlog.get_logger()


class GrpcEmbeddingClient:

    def __init__(self, grpc_url: str, default_model: str) -> None:
        self._grpc_url = grpc_url
        self._default_model = default_model
        self._channel: grpc.aio.Channel | None = None
        self._stub: embedding_pb2_grpc.EmbeddingServiceStub | None = None
        self._stream: grpc.aio.StreamStreamCall | None = None

    async def embed(self, text: str) -> list[float]:
        """Send one text, return one vector. Reconnects once on stream error."""
        for attempt in range(2):
            try:
                await self._ensure_stream()
                corr_id = str(uuid.uuid4())
                await self._stream.write(
                    embedding_pb2.EmbedStreamRequest(
                        correlation_id=corr_id,
                        text=text,
                        model=self._default_model,
                    )
                )
                response = await self._stream.read()
                if response == grpc.aio.EOF:
                    raise grpc.RpcError("stream closed by server")
                if response.correlation_id != corr_id:
                    raise grpc.RpcError(
                        f"stream out of sync: expected correlation_id={corr_id!r}, "
                        f"got {response.correlation_id!r}"
                    )
                return list(response.vector)
            except grpc.RpcError as exc:
                log.warning(
                    "grpc_embed_stream_error",
                    attempt=attempt,
                    error=str(exc),
                )
                await self._reset_stream()
                if attempt == 1:
                    raise

        raise RuntimeError("unreachable")

    async def aclose(self) -> None:
        await self._reset_stream()
        if self._channel is not None:
            await self._channel.close()
            self._channel = None

    async def _ensure_stream(self) -> None:
        if self._stream is not None:
            return
        if self._channel is None:
            self._channel = grpc.aio.insecure_channel(
                self._grpc_url,
                options=[
                    ("grpc.keepalive_time_ms", 60_000),
                    ("grpc.keepalive_timeout_ms", 20_000),
                    ("grpc.keepalive_permit_without_calls", True),
                ],
            )
            self._stub = embedding_pb2_grpc.EmbeddingServiceStub(self._channel)
        self._stream = self._stub.EmbedStream()
        log.info("grpc_embed_stream_opened", url=self._grpc_url)

    async def _reset_stream(self) -> None:
        if self._stream is not None:
            try:
                await self._stream.done_writing()
            except Exception:
                pass
            self._stream = None
