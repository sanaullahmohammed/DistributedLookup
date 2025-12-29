#!/bin/bash

API_URL="http://localhost:8080/api/lookup"

echo "========================================="
echo "Distributed Lookup System - API Tests"
echo "========================================="
echo ""

# Test 1: Submit a job for IP address
echo "Test 1: Submit lookup for IP address (8.8.8.8)"
echo "----------------------------------------"
RESPONSE=$(curl -s -X POST "$API_URL" \
  -H "Content-Type: application/json" \
  -d '{"target": "8.8.8.8"}')

echo "Response:"
echo "$RESPONSE"
JOB_ID=$(echo "$RESPONSE" | sed -n 's/.*"jobId"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
echo ""

sleep 2

# Test 2: Check job status
echo "Test 2: Check job status"
echo "----------------------------------------"
curl -s "$API_URL/$JOB_ID"
echo ""

# Test 3: Submit a job for domain
echo "Test 3: Submit lookup for domain (google.com)"
echo "----------------------------------------"
RESPONSE2=$(curl -s -X POST "$API_URL" \
  -H "Content-Type: application/json" \
  -d '{"target": "google.com", "services": [0, 1]}')

echo "Response:"
echo "$RESPONSE2"
JOB_ID2=$(echo "$RESPONSE2" | sed -n 's/.*"jobId"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
echo ""

# Test 4: List available services
echo "Test 4: List available services"
echo "----------------------------------------"
curl -s "$API_URL/services"
echo ""

echo "Waiting 5 seconds for jobs to complete..."
sleep 5

# Test 5: Check first job final status
echo "Test 5: Check first job final status"
echo "----------------------------------------"
curl -s "$API_URL/$JOB_ID"
echo ""

# Test 6: Check second job final status
echo "Test 6: Check second job final status"
echo "----------------------------------------"
curl -s "$API_URL/$JOB_ID2"
echo ""

echo "========================================="
echo "Tests completed!"
echo "========================================="
