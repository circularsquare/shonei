#!/bin/bash
cd /c/Users/anita/projects/shonei-server
go run main.go &
SERVER_PID=$!

echo "Server started (PID: $SERVER_PID)"
trap "kill $SERVER_PID 2>/dev/null" EXIT

cd client
go run main.go
