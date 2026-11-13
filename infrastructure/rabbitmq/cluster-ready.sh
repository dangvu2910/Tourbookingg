#!/usr/bin/env bash
set -euo pipefail
for attempt in $(seq 1 60); do
  count=$(rabbitmqctl -q -n rabbit@rabbitmq-a --erlang-cookie "$RABBITMQ_ERLANG_COOKIE" eval 'length(rabbit_nodes:list_running()).')
  if [ "$count" = "3" ]; then
    # Persisted test queues may have been declared before every node joined.
    rabbitmq-queues -n rabbit@rabbitmq-a --erlang-cookie "$RABBITMQ_ERLANG_COOKIE" grow rabbit@rabbitmq-b all
    rabbitmq-queues -n rabbit@rabbitmq-a --erlang-cookie "$RABBITMQ_ERLANG_COOKIE" grow rabbit@rabbitmq-c all
    exit 0
  fi
  sleep 2
done
echo "RabbitMQ cluster did not reach three running members" >&2
exit 1
