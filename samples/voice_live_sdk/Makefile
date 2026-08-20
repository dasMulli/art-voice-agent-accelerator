# ============================================================================
# VoiceLive SDK - Hello World Demo Makefile
# ============================================================================

.PHONY: help install run clean test check-env check-audio

# Default target
help:
	@echo "======================================================================"
	@echo "🎙️  VoiceLive SDK - Hello World Demo"
	@echo "======================================================================"
	@echo ""
	@echo "Available commands:"
	@echo "  make install      - Install dependencies (PyAudio + VoiceLive SDK)"
	@echo "  make check-env    - Check if .env file is configured"
	@echo "  make check-audio  - Check audio devices (microphone/speakers)"
	@echo "  make run          - Run the voice assistant"
	@echo "  make test         - Test run with verbose logging"
	@echo "  make clean        - Clean up logs and cache"
	@echo ""
	@echo "Quick Start:"
	@echo "  1. make install"
	@echo "  2. Add your API key to .env file"
	@echo "  3. make run"
	@echo ""

# Install dependencies
install:
	@echo "🔧 Installing dependencies for macOS..."
	@echo ""
	@echo "Checking for Homebrew..."
	@which brew > /dev/null || (echo "❌ Homebrew not found. Install from https://brew.sh" && exit 1)
	@echo "✅ Homebrew found"
	@echo ""
	@echo "Installing PortAudio (required for PyAudio)..."
	brew list portaudio &>/dev/null || brew install portaudio
	@echo "✅ PortAudio installed"
	@echo ""
	@echo "Installing Python dependencies..."
	pip install pyaudio python-dotenv
	pip install azure-ai-voicelive
	@echo ""
	@echo "✅ All dependencies installed!"
	@echo ""
	@echo "Next step: Add your API key to .env file"

# Check environment configuration
check-env:
	@echo "🔍 Checking environment configuration..."
	@if [ ! -f .env ]; then \
		echo "❌ .env file not found!"; \
		echo "   Create one with: cp .env.example .env"; \
		exit 1; \
	fi
	@if ! grep -q 'AZURE_VOICELIVE_API_KEY="..*"' .env 2>/dev/null; then \
		echo "⚠️  AZURE_VOICELIVE_API_KEY is empty in .env file"; \
		echo "   Please add your API key before running."; \
		exit 1; \
	fi
	@echo "✅ Environment configured"

# Check audio devices
check-audio:
	@echo "🔊 Checking audio devices..."
	@python3 -c "import pyaudio; p = pyaudio.PyAudio(); \
		inputs = [i for i in range(p.get_device_count()) if p.get_device_info_by_index(i).get('maxInputChannels', 0) > 0]; \
		outputs = [i for i in range(p.get_device_count()) if p.get_device_info_by_index(i).get('maxOutputChannels', 0) > 0]; \
		print('✅ Input devices found:', len(inputs)); \
		print('✅ Output devices found:', len(outputs)); \
		p.terminate()" || (echo "❌ Audio check failed" && exit 1)

# Run the voice assistant
run: check-env check-audio
	@echo "======================================================================"
	@echo "🚀 Starting Voice Assistant..."
	@echo "======================================================================"
	@echo ""
	@chmod +x helloworld.py
	@./helloworld.py

# Test run with verbose logging
test: check-env check-audio
	@echo "======================================================================"
	@echo "🧪 Testing Voice Assistant (Verbose Mode)..."
	@echo "======================================================================"
	@echo ""
	@chmod +x helloworld.py
	@./helloworld.py --verbose

# Run with token credential (Azure CLI auth)
run-with-token:
	@echo "======================================================================"
	@echo "🔐 Starting Voice Assistant with Azure Token Auth..."
	@echo "======================================================================"
	@echo ""
	@chmod +x helloworld.py
	@./helloworld.py --use-token-credential

# Clean up logs and cache
clean:
	@echo "🧹 Cleaning up..."
	rm -rf logs/
	rm -rf __pycache__/
	rm -rf *.pyc
	@echo "✅ Cleanup complete"

# Show logs from last run
logs:
	@if [ -d logs ]; then \
		echo "📋 Recent logs:"; \
		ls -lt logs/ | head -5; \
		echo ""; \
		echo "To view latest log:"; \
		echo "  tail -f logs/\$$(ls -t logs/ | head -1)"; \
	else \
		echo "📋 No logs found. Run 'make run' first."; \
	fi

# Quick setup guide
setup:
	@echo "======================================================================"
	@echo "🛠️  VoiceLive SDK Setup Guide"
	@echo "======================================================================"
	@echo ""
	@echo "Step 1: Install dependencies"
	@echo "  make install"
	@echo ""
	@echo "Step 2: Configure API key"
	@echo "  Edit .env file and add:"
	@echo "  AZURE_VOICELIVE_API_KEY=\"your-key-here\""
	@echo ""
	@echo "Step 3: Grant microphone permissions"
	@echo "  System Settings → Privacy & Security → Microphone"
	@echo "  Enable for Terminal/iTerm"
	@echo ""
	@echo "Step 4: Run the demo"
	@echo "  make run"
	@echo ""
