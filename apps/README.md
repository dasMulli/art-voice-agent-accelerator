# **ARTVoice Applications**

**Real-time voice agents** with FastAPI backend and React frontend. Multi-agent orchestration with Azure Communication Services integration.

## **Architecture**

```
Phone/Browser → ACS/WebSocket → FastAPI Backend → Multi-Agent AI → Azure Services
```

## **Structure**

# TODO: Update with latest folder structure
```
apps/artagent/
├── backend/           # FastAPI + multi-agent framework
│   ├── main.py       # 🚀 Entry point
│   ├── agents/   # 🤖 ARTAgent, LVAgent, FoundryAgents
│   ├── api/          # 🌐 REST/WebSocket endpoints
│   └── config/       # ⚙️ Voice, features, limits
├── frontend/         # React + WebSocket client
│   ├── src/components/  # 🎨 UI and voice controls
│   └── src/hooks/      # 🔗 WebSocket and audio hooks
└── scripts/          # 🛠️ Development utilities
```

## **Quick Reference**

| **Task** | **Location** |
|----------|--------------|
| Start backend | `backend/main.py` |
| Start frontend | `frontend/src/main.jsx` |
| Agent configs | `backend/src/agents/*/agent_store/` |
| Voice settings | `backend/config/voice_config.py` |
| UI components | `frontend/src/components/` |
| Environment setup | `.env.sample` files |

## **Key Endpoints**

- **WebSocket**: `/api/v1/media/stream` - Real-time audio
- **ACS Events**: `/api/v1/acs/events` - Phone call handling
- **Health**: `/health` - Service status

### **Call Processing Pipeline**
1. **Caller dials Azure phone number** → ACS receives call
2. **ACS sends webhook** to backend with call details
3. **Backend answers call** via ACS Call Automation API
4. **Audio stream established** from ACS to backend WebSocket
5. **Real-time processing** - Speech-to-Text → Agent → Text-to-Speech
6. **Audio response** streamed back to caller via ACS

### **Frontend "Call Me" Feature**
- **Direct browser calling** using @azure/communication-calling SDK
- **No phone number required** - browser-to-backend voice connection
- **Same processing pipeline** as phone calls, different entry point

## **🔧 Technical Stack**

### **Frontend (React)**
- **WebSocket Client**: Real-time communication with FastAPI backend
- **Audio Processing**: Web Audio API for microphone capture and playback
- **ACS Integration**: @azure/communication-calling for direct browser calls
- **UI Components**: Real-time conversation display and voice controls

### **Backend (FastAPI)**
- **WebSocket Server**: Handles real-time audio streams and conversation management
- **ARTAgent Framework**: Multi-agent orchestration (Auth, FNOL, General, Billing)
- **ACS Integration**: Call Automation API for telephony, Media Streaming for audio
- **Azure Services**: Speech SDK, OpenAI, Redis, CosmosDB integration

### **Key Endpoints**
#TODO: Update with latest endpoint schema
| **Endpoint** | **Purpose** | **Type** |
|--------------|-------------|----------|
| `WS /api/v1/realtime/conversation` | Frontend voice interaction | WebSocket |
| `WS /api/v1/media/stream` | ACS audio streaming | WebSocket |
| `POST /api/v1/acs/events` | ACS call event webhooks | REST |
| `GET /api/v1/health` | System health monitoring | REST |
| `GET /api/v1/agents` | Agent configuration | REST |

## **🚀 Quick Start**

### **Prerequisites**
- Python 3.11+, Node.js 18+
- [uv](https://docs.astral.sh/uv/) (recommended) or pip
- Azure services provisioned (see Infrastructure section)

### **Backend Setup**
```bash
# From project root (recommended)
uv sync
cp .env.sample .env  # Configure Azure credentials
uv run uvicorn apps.artagent.backend.main:app --host 0.0.0.0 --port 8010 --reload
```

### **Frontend Setup**  
```bash
cd apps/artagent/frontend
npm install && npm run dev  # Starts on localhost:5173
```

### **Access Points**
- **Web UI**: http://localhost:5173
- **API Docs**: http://localhost:8010/docs
- **WebSocket**: ws://localhost:8010

## **⚙️ Configuration**

### **Environment Variables**
```bash
# Backend (.env)
AZURE_OPENAI_ENDPOINT=your-endpoint
AZURE_SPEECH_KEY=your-speech-key  
ACS_CONNECTION_STRING=your-acs-connection
REDIS_CONNECTION_STRING=your-redis-string

# Frontend (.env)
VITE_BACKEND_URL=ws://localhost:8010
```

## **🏭 Infrastructure Requirements**

### **Required Azure Services**
- **Azure Communication Services** - Phone numbers, call automation, media streaming
- **Azure Speech Services** - Real-time STT/TTS processing  
- **Azure OpenAI** - GPT models for agent responses
- **Azure Redis Cache** - Session state and conversation memory
- **Azure Cosmos DB** - Conversation history and persistent data

### **Deployment Options**
- **Terraform**: `infra/terraform/` - Automated provisioning
- **Azure Developer CLI**: `azd up` - Quick deployment
- **Manual**: Azure Portal setup

### **Local Development with ACS**
```bash
make start_tunnel # Exposes backend for ACS webhooks via Devtunnel
```
Update `BASE_URL` environment variable with tunnel URL.

### **Useful Scripts**
- `scripts/start_backend.py` - Backend with validation
- `scripts/start_frontend.sh` - React dev server
- `scripts/start_devtunnel_host.sh` - ACS integration tunnel

**📖 Detailed Documentation**: [Deployment Guide](../../docs/DeploymentGuide.md)


