#!/bin/bash
cd /c/Users/anita/projects/shonei-server
./shonei-server &
SERVER_PID=$!

echo "Server started (PID: $SERVER_PID)"
trap "kill $SERVER_PID 2>/dev/null" EXIT

echo "Waiting for server to be ready..."
until curl -s --max-time 1 http://localhost:8082/ -o /dev/null 2>/dev/null; do
  # Exit if server process died
  kill -0 $SERVER_PID 2>/dev/null || { echo "Server process died"; exit 1; }
  sleep 0.5
done

cd client
go run main.go
