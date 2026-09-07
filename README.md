# MateOS

An AI-native team collaboration platform where humans and AI agents work together through shared channels, memory, and context.

## Overview

MateOS enables seamless collaboration between human users and AI agents by providing:

- **Shared Channels**: Create and manage collaborative spaces where humans and agents interact
- **Intelligent Memory System**: Persistent storage of conversations, facts, preferences, and decisions with importance scoring
- **Shared Context**: Maintain conversation state and context across participants for coherent multi-agent interactions
- **Message Management**: Rich message types (Text, System, AgentResponse, ContextUpdate) with metadata support
- **User Management**: Support for both human and AI agent users in the system

## Architecture

The project is structured as a .NET 8.0 solution with:

### `MateOS.Core`
Contains domain models, interfaces, and service implementations:
- **Entities**: `User`, `Channel`, `Message`, `Memory`, `Context`
- **Interfaces**: Service contracts for `IUserService`, `IChannelService`, `IMessageService`, `IMemoryService`, `IContextService`
- **Services**: In-memory implementations of core services
- **DTOs**: Data transfer objects for API communication

### `MateOS.Api`
ASP.NET Core Web API providing REST endpoints:
- `UsersController`: Manage users (human and AI agents)
- `ChannelsController`: Create and manage collaboration channels
- `MessagesController`: Post and retrieve messages
- `MemoriesController`: Store and search shared memories
- `ContextsController`: Manage conversation contexts

## Building and Running

```bash
# Build the solution
dotnet build

# Run the API
dotnet run --project src/MateOS.Api/MateOS.Api.csproj

# The API will be available at https://localhost:5001
```

## API Endpoints

### Users
- `POST /api/users` - Create a user (Human or Agent)
- `GET /api/users` - List users (with optional type filter)
- `GET /api/users/{id}` - Get user details
- `PUT /api/users/{id}` - Update user
- `DELETE /api/users/{id}` - Delete user

### Channels
- `POST /api/channels` - Create a channel
- `GET /api/channels` - List channels
- `GET /api/channels/{id}` - Get channel details
- `POST /api/channels/{id}/members/{userId}` - Add member to channel
- `DELETE /api/channels/{id}/members/{userId}` - Remove member from channel
- `PUT /api/channels/{id}` - Update channel
- `DELETE /api/channels/{id}` - Delete channel

### Messages
- `POST /api/messages` - Create a message
- `GET /api/messages/{id}` - Get message
- `GET /api/messages/channel/{channelId}` - Get channel messages
- `GET /api/messages/user/{userId}` - Get user messages
- `PUT /api/messages/{id}` - Update message
- `DELETE /api/messages/{id}` - Delete message

### Memories
- `POST /api/memories` - Create a memory
- `GET /api/memories/{id}` - Get memory
- `GET /api/memories/user/{userId}` - Get user memories
- `GET /api/memories/channel/{channelId}` - Get channel memories
- `GET /api/memories/search?query=...` - Search memories
- `PUT /api/memories/{id}` - Update memory
- `DELETE /api/memories/{id}` - Delete memory

### Contexts
- `POST /api/contexts` - Create a context
- `GET /api/contexts/{id}` - Get context
- `GET /api/contexts/channel/{channelId}` - Get channel context
- `POST /api/contexts/{id}/memories/{memoryId}` - Add memory to context
- `DELETE /api/contexts/{id}/memories/{memoryId}` - Remove memory from context
- `PUT /api/contexts/{id}/state` - Update shared state
- `PUT /api/contexts/{id}` - Update context
- `DELETE /api/contexts/{id}` - Delete context

## Core Concepts

### Users
Participants in the system, either humans or AI agents. Each user has:
- Unique ID
- Name and email
- Type (Human or Agent)
- Creation and update timestamps

### Channels
Collaborative spaces where humans and agents interact. Features:
- Member management
- Privacy control (public/private)
- Descriptions for context

### Messages
Communications within channels with support for:
- Text messages
- System messages
- Agent responses
- Context updates

### Memory
Persistent knowledge storage with types:
- **Conversation**: Dialogue history
- **Fact**: Factual knowledge
- **Preference**: User/agent preferences
- **Context**: Environmental state
- **Decision**: Important decisions made

Memories include importance scoring for prioritization.

### Context
Maintains shared state for conversations:
- Participant tracking
- Relevant memory references
- Shared state dictionary
- Topic information

## License

MIT License - See LICENSE file for details
