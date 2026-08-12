import urllib.error
import urllib.request
from unittest.mock import MagicMock, patch

from health import kafka_is_reachable, start_health_server


def test_kafka_is_reachable_returns_true_when_list_topics_succeeds() -> None:
    with patch("health.AdminClient") as mock_admin_cls:
        mock_admin_cls.return_value.list_topics.return_value = MagicMock()
        assert kafka_is_reachable("localhost:9092") is True


def test_kafka_is_reachable_returns_false_when_list_topics_raises() -> None:
    with patch("health.AdminClient") as mock_admin_cls:
        mock_admin_cls.return_value.list_topics.side_effect = Exception("boom")
        assert kafka_is_reachable("localhost:9092") is False


def test_health_endpoint_always_ok() -> None:
    server = start_health_server("localhost:9092", 0)
    try:
        port = server.server_address[1]
        response = urllib.request.urlopen(f"http://localhost:{port}/health")
        assert response.status == 200
    finally:
        server.shutdown()


def test_ready_endpoint_returns_503_when_kafka_unreachable() -> None:
    with patch("health.kafka_is_reachable", return_value=False):
        server = start_health_server("localhost:9092", 0)
        try:
            port = server.server_address[1]
            try:
                urllib.request.urlopen(f"http://localhost:{port}/ready")
                assert False, "expected HTTPError for 503 response"
            except urllib.error.HTTPError as exc:
                assert exc.code == 503
        finally:
            server.shutdown()


def test_ready_endpoint_returns_200_when_kafka_reachable() -> None:
    with patch("health.kafka_is_reachable", return_value=True):
        server = start_health_server("localhost:9092", 0)
        try:
            port = server.server_address[1]
            response = urllib.request.urlopen(f"http://localhost:{port}/ready")
            assert response.status == 200
        finally:
            server.shutdown()
