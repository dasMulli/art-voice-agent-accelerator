# Card Decline API - Quick Reference

## 🚀 Quick Start

### Local Development
```bash
cd apps/cardapi
./start.sh       # Start MCP server
```

The MCP server loads data from `database/decline_codes_policy_pack.json` locally.

### Docker
```bash
cd apps/cardapi

# Build and run MCP (self-contained)
docker build -f Dockerfile.mcp -t cardapi-mcp .
docker run -p 80:80 cardapi-mcp
```

For Azure Cosmos DB connection:
```bash
docker run \
  -e AZURE_COSMOS_DATABASE_NAME=cardapi \
  -e AZURE_COSMOS_COLLECTION_NAME=declinecodes \
  -p 80:80 cardapi-mcp
```

### Azure Deployment
```bash
# From project root
azd deploy cardapi-mcp
```

## 📋 API Endpoints

| Endpoint | Method | Description | Example |
|----------|--------|-------------|---------|
| `/health` | GET | Health check | `curl localhost:8000/health` |
| `/api/v1/codes` | GET | Get all codes | `curl localhost:8000/api/v1/codes` |
| `/api/v1/codes/{code}` | GET | Get specific code | `curl localhost:8000/api/v1/codes/51` |
| `/api/v1/search` | GET | Search codes | `curl "localhost:8000/api/v1/search?q=PIN"` |
| `/api/v1/metadata` | GET | Get metadata | `curl localhost:8000/api/v1/metadata` |

## 🔍 Common Decline Codes

### Numeric (Base24)
- **51** - Insufficient funds
- **55** - Invalid PIN
- **61** - Daily limit reached
- **75** - Max invalid PIN tries
- **94** - Duplicate transaction

### Alphanumeric (FAST)
- **C1** - Card status closed
- **RT** - Fraud protection scoring
- **K1** - Client lock
- **CV** - CVV verification failed
- **S4** - Account status closed

## 🔧 Query Parameters

### Filter by Type
```bash
# Numeric codes only (Base24)
curl "localhost:8000/api/v1/codes?code_type=numeric"

# Alphanumeric codes only (FAST)
curl "localhost:8000/api/v1/codes?code_type=alphanumeric"
```

### Search Examples
```bash
# Search for insufficient funds
curl "localhost:8000/api/v1/search?q=insufficient"

# Search for PIN issues
curl "localhost:8000/api/v1/search?q=PIN"

# Search fraud-related
curl "localhost:8000/api/v1/search?q=fraud"

# Search with type filter
curl "localhost:8000/api/v1/search?q=expired&code_type=numeric"
```

## 📊 Response Format

### Individual Code
```json
{
  "code": "51",
  "description": "Insufficient funds",
  "information": "Account does not have available funds.",
  "actions": [
    "Transfer funds if applicable",
    "Use another payment method"
  ],
  "code_type": "numeric"
}
```

### Code List
```json
{
  "codes": [
    { "code": "51", ... },
    { "code": "55", ... }
  ],
  "total": 2
}
```

## 🤖 MCP Tools

When integrated with AI agents:

### 1. lookup_decline_code
```
Input: { "code": "51" }
Output: Full details for code 51
```

### 2. search_decline_codes
```
Input: { "query": "PIN", "code_type": "numeric" }
Output: All PIN-related numeric codes
```

### 3. get_all_decline_codes
```
Input: { "code_type": "alphanumeric" }
Output: All alphanumeric codes
```

### 4. get_decline_codes_metadata
```
Input: {}
Output: Database statistics and info
```

## 🐛 Troubleshooting

### MCP server won't start
```bash
# Check if port is in use
lsof -i :80

# Check logs
tail -f apps/cardapi/mcp.log

# Verify database exists
ls -la apps/cardapi/database/decline_codes_policy_pack.json
```

### Docker build fails
```bash
# Ensure you're in the right directory
cd apps/cardapi

# Check Docker is running
docker ps

# Build with verbose output
docker build -f Dockerfile.mcp --progress=plain -t cardapi-mcp .
```

### Azure Cosmos DB connection issues
```bash
# Check environment variables
echo $AZURE_COSMOS_DATABASE_NAME
echo $AZURE_COSMOS_COLLECTION_NAME

# Verify managed identity (in Azure)
echo $AZURE_CLIENT_ID
```

## 📁 Project Structure

```
apps/cardapi/
├── mcp_app/
│   ├── service.py          # MCP server (self-contained)
│   ├── requirements.txt    # Python dependencies
│   └── __init__.py
├── database/
│   └── decline_codes_policy_pack.json  # Local decline codes database
├── scripts/
│   └── provision_data.py   # Cosmos DB data provisioning
├── Dockerfile.mcp          # MCP container
├── README.md               # Full documentation
├── QUICKSTART.md           # This file
└── start.sh                # Local dev startup
```

## 🔗 Related Files

- [CARDAPI_MCP_UPDATES.md](mcp_app/CARDAPI_MCP_UPDATES.md) - MCP server details
- `infra/terraform/cardapi.tf` - Azure infrastructure
- `azure.yaml` - Deployment configuration

## 📚 Documentation

- Detailed README: [README.md](README.md)
- MCP Updates: [CARDAPI_MCP_UPDATES.md](mcp_app/CARDAPI_MCP_UPDATES.md)

## 💡 Tips

1. **Use MCP tools**: AI agents can query decline codes via MCP protocol
2. **Filter by code type**: Use numeric/alphanumeric to narrow results
3. **Check metadata first**: Get counts and info before querying all codes
4. **Monitor logs**: Check Application Insights in Azure for production issues
5. **Local data**: In dev mode, data loads from JSON file (no network required)

## 🆘 Support

- Check logs: `mcp.log`
- Run the start script: `./start.sh`
- Review errors in Application Insights (Azure deployment)
