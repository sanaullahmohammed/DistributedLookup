# System Flow Diagrams

## 1. High-Level Architecture

```mermaid
graph TD
    %% Nodes
    Client[Client Application]

    subgraph API_Service [API Service ASP.NET Core]
        Controller["LookupController<br/>- SubmitLookupJob (use case)<br/>- GetJobStatus (use case)"]
    end

    RabbitMQ{{"RabbitMQ<br/>(Events & Commands)"}}
    Redis[("Redis<br/>(State)")]

    %% Workers
    Saga["Saga (State Machine)"]
    Geo["GeoWorker<br/>(x2)"]
    Ping["PingWorker<br/>(x2)"]
    Rdap["RdapWorker<br/>(x2)"]
    Rdap["ReverseDNS<br/>(x2)"]

    %% External Systems
    ExtIP["ip-api.com"]
    ExtNet["Network Stack"]
    ExtRdap["RDAP Servers"]
    ExtRdns["DNS Servers"]

    %% Connections: Client to API
    Client -->|POST /api/lookup<br/>submit job| Controller
    Client -->|GET /api/lookup/id<br/>poll status| Controller

    %% Connections: API to Infra
    Controller -->|"Publish event"| RabbitMQ
    Controller -->|"Read state"| Redis

    %% Connections: RabbitMQ to Consumers
    RabbitMQ -->|Consume| Saga
    RabbitMQ -->|Consume| Geo
    RabbitMQ -->|Consume| Ping
    RabbitMQ -->|Consume| Rdap
    RabbitMQ -->|Consume| Rdns

    %% Connections: Workers to Targets
    Saga -->|Update| Redis
    Geo -->|"External API"| ExtIP
    Ping -->|ICMP| ExtNet
    Rdap -->|"RDAP API"| ExtRdap
    Rdns -->|"DNS API"| ExtRdns

    %% Styling for better visibility
    classDef storage fill:#eee,stroke:#333,stroke-width:2px;
    class Redis storage;
    classDef queue fill:#f9f,stroke:#333,stroke-width:2px;
    class RabbitMQ queue;
```

## 2. Message Flow (Sequence Diagram)

```mermaid
sequenceDiagram
    autonumber
    participant Client
    participant API
    participant RabbitMQ
    participant Saga
    participant Workers
    participant Redis

    Note over Client, Redis: 1. Job Submission Phase
    
    Client->>API: POST /api/lookup (Job)
    activate API
    API->>Redis: Save Job (Pending)
    API->>RabbitMQ: Publish JobSubmitted
    API-->>Client: 202 Accepted + JobId
    deactivate API

    Note over RabbitMQ, Redis: 2. Orchestration & Processing

    RabbitMQ->>Saga: Consume JobSubmitted
    activate Saga
    Saga->>RabbitMQ: Dispatch Commands (CheckGeoIP, etc.)
    deactivate Saga

    RabbitMQ->>Workers: Consume Command
    activate Workers
    Workers->>Workers: External API Call
    Workers->>RabbitMQ: Publish TaskCompleted
    deactivate Workers

    RabbitMQ->>Saga: Consume TaskCompleted
    activate Saga
    Saga->>Redis: Update Job (Add Result)
    deactivate Saga

    Note over Client, Redis: 3. Polling (Partial Status)

    Client->>API: GET /api/lookup/{jobId}
    activate API
    API->>Redis: Get Job
    Redis-->>API: Job Data
    API-->>Client: 200 OK (Partial)
    deactivate API

    Note over RabbitMQ, Redis: ... Workers complete other tasks ...

    RabbitMQ->>Saga: Consume Last TaskCompleted
    activate Saga
    Saga->>Redis: Update Job (Complete!)
    deactivate Saga

    Note over Client, Redis: 4. Polling (Final Status)

    Client->>API: GET /api/lookup/{jobId}
    activate API
    API->>Redis: Get Job
    Redis-->>API: Job Data
    API-->>Client: 200 OK (Complete)
    deactivate API
```

## 3. Direct Worker Persistence Flow 

```mermaid
sequenceDiagram
    participant W as Worker
    participant Store as IWorkerResultStore
    participant RMQ as RabbitMQ
    participant Saga as Saga Machine
    participant Redis as Redis

    Note over W,Redis: New Pattern: Direct Persistence
    
    W->>W: Perform Lookup
    W->>Store: SaveResultAsync(data)
    Store->>Redis: Save result data
    Redis-->>Store: Confirmation
    Store-->>W: Return ResultLocation
    
    W->>RMQ: Publish TaskCompleted<br/>(with ResultLocation, no data)
    RMQ->>Saga: Deliver event
    Saga->>Saga: Track completion
    Saga->>Redis: Update job status
    
    Note over W,Redis: Data saved BEFORE notification
    Note over W,Redis: Events contain location, not data
```

## 4. Worker Base Class Pattern 

```mermaid
classDiagram
    class LookupWorkerBase~TCommand~ {
        <<abstract>>
        #Logger: ILogger
        #ResultStore: IWorkerResultStore
        +Consume(ConsumeContext) Task
        #PerformLookupAsync(TCommand)* Task~object~
        #ValidateTarget(TCommand) string?
        #ServiceType* ServiceType
    }
    
    class GeoIPConsumer {
        -httpClient: HttpClient
        #ServiceType: ServiceType
        #PerformLookupAsync(CheckGeoIP) Task~object~
    }
    
    class PingConsumer {
        #ServiceType: ServiceType
        #PerformLookupAsync(CheckPing) Task~object~
    }
    
    class RdapConsumer {
        -httpClient: HttpClient
        #ServiceType: ServiceType
        #PerformLookupAsync(CheckRDAP) Task~object~
    }
    
    class ReverseDnsConsumer {
        #ServiceType: ServiceType
        #PerformLookupAsync(CheckReverseDNS) Task~object~
        #ValidateTarget(CheckReverseDNS) string?
    }
    
    LookupWorkerBase~TCommand~ <|-- GeoIPConsumer
    LookupWorkerBase~TCommand~ <|-- PingConsumer
    LookupWorkerBase~TCommand~ <|-- RdapConsumer
    LookupWorkerBase~TCommand~ <|-- ReverseDnsConsumer
    
    note for LookupWorkerBase~TCommand~ "Template Method Pattern:\n1. Timing\n2. Validation\n3. PerformLookupAsync\n4. Save to Store\n5. Publish Event\n6. Error Handling"
    
    note for GeoIPConsumer "Only implements:\nPerformLookupAsync()\n\nBase class handles:\neverything else!"
```

## 5. Polymorphic Storage Architecture 

```mermaid
classDiagram
    class ResultLocation {
        <<abstract>>
        +StorageType* StorageType
    }
    
    class RedisResultLocation {
        +StorageType: StorageType
        +Key: string
        +Database: int
        +Ttl: TimeSpan?
    }
    
    class S3ResultLocation {
        +StorageType: StorageType
        +Bucket: string
        +Key: string
        +Region: string?
        +PresignedUrl: string?
    }
    
    class DynamoDBResultLocation {
        +StorageType: StorageType
        +TableName: string
        +PartitionKey: string
        +SortKey: string?
    }
    
    class FileSystemResultLocation {
        +StorageType: StorageType
        +Path: string
    }
    
    class AzureBlobResultLocation {
        +StorageType: StorageType
        +ContainerName: string
        +BlobName: string
        +SasUrl: string?
    }
    
    ResultLocation <|-- RedisResultLocation
    ResultLocation <|-- S3ResultLocation
    ResultLocation <|-- DynamoDBResultLocation
    ResultLocation <|-- FileSystemResultLocation
    ResultLocation <|-- AzureBlobResultLocation
    
    note for ResultLocation "JSON Polymorphism:\n$type discriminator"
    note for RedisResultLocation "Current Implementation"
    note for S3ResultLocation "Future: Large results"
    note for DynamoDBResultLocation "Future: Structured data"
```

## 6. Storage Abstraction Flow 

```mermaid
flowchart TD
    Worker[Worker] --> |Uses| Resolver[IWorkerResultStoreResolver]
    Resolver --> |Returns| Store[IWorkerResultStore]
    Store --> |Implements| Redis[RedisWorkerResultStore]
    Store --> |Implements| S3[S3WorkerResultStore]
    Store --> |Implements| Dynamo[DynamoDBWorkerResultStore]
    
    Redis --> |Saves to| RedisDB[(Redis)]
    S3 --> |Saves to| S3Bucket[(S3 Bucket)]
    Dynamo --> |Saves to| DynamoDB[(DynamoDB)]
    
    Redis --> |Returns| RedisLoc[RedisResultLocation]
    S3 --> |Returns| S3Loc[S3ResultLocation]
    Dynamo --> |Returns| DynamoLoc[DynamoDBResultLocation]
    
    RedisLoc --> |Stored in| Saga[Saga State]
    S3Loc --> |Stored in| Saga
    DynamoLoc --> |Stored in| Saga
    
    style Redis fill:#f9f,stroke:#333,stroke-width:2px
    style S3 fill:#eee,stroke:#333,stroke-width:2px,stroke-dasharray: 5 5
    style Dynamo fill:#eee,stroke:#333,stroke-width:2px,stroke-dasharray: 5 5
    
    classDef future fill:#eee,stroke:#333,stroke-dasharray: 5 5
    class S3,Dynamo,S3Bucket,DynamoDB,S3Loc,DynamoLoc future
```

## 7. State Machine (Saga)

```mermaid
flowchart TD
    %% Nodes
    Init([Initial])
    Proc[Processing]
    Check{All Complete?}
    Done([Completed])

    %% Transitions
    Init -->|JobSubmitted| Proc
    
    Proc -->|TaskCompleted| Check
    
    %% Loop Logic
    Check -->|No| Proc
    Check -->|Yes| Done

    %% Styling
    style Init fill:#f9f,stroke:#333,stroke-width:2px
    style Done fill:#9f9,stroke:#333,stroke-width:2px
    style Check fill:#ff9,stroke:#333,stroke-width:2px
```

## 8. Worker Processing Flow

```mermaid
flowchart TD
    subgraph Worker_Lifecycle [Worker Lifecycle]
        direction TB
        
        %% Nodes
        Consume[Consume Command<br/>from Queue]
        Validate[Validate Input]
        Lookup[Perform Lookup<br/>GeoIP/Ping/RDAP/ReverseDNS]
        
        %% Decision
        Check{Result?}
        
        %% Branches
        Success[Package<br/>Success Result]
        Failure[Package<br/>Error Message]
        
        %% Convergence
        Publish[Publish<br/>TaskCompleted<br/>Event]

        %% Connections
        Consume --> Validate
        Validate --> Lookup
        Lookup --> Check
        
        Check -->|Success| Success
        Check -->|Failure| Failure
        
        Success --> Publish
        Failure --> Publish
    end

    %% Styling
    style Check fill:#ff9,stroke:#333,stroke-width:2px
    style Success fill:#d4edda,stroke:#155724
    style Failure fill:#f8d7da,stroke:#721c24
    style Publish fill:#eee,stroke:#333,stroke-width:2px,stroke-dasharray: 5 5
```

## 9. Data Storage Model

### Redis Key Structure

```
lookup:job:{jobId}
├─ jobId: string
├─ target: string
├─ targetType: enum
├─ status: enum (Pending/Processing/Completed/Failed)
├─ createdAt: datetime
├─ completedAt: datetime?
├─ requestedServices: ServiceType[]
└─ results: ServiceResult[]
   ├─ serviceType: enum
   ├─ success: bool
   ├─ data: json
   ├─ errorMessage: string?
   ├─ completedAt: datetime
   └─ durationMs: int

saga:{jobId}
├─ correlationId: guid
├─ currentState: string
├─ pendingServices: ServiceType[]
└─ completedServices: ServiceType[]

result:{jobId}:{serviceType}  
├─ data: json (actual result data)
└─ ttl: 3600 seconds
```

## 10. Scaling Model

```mermaid
flowchart TD
    %% Top Level
    LB{{"Load Balancer<br/>(optional)"}}

    %% API Cluster
    subgraph API_Layer [API Cluster]
        API1["API 1"]
        API2["API 2"]
        API3["API 3"]
    end

    %% Message Broker
    RMQ[("RabbitMQ<br/>(Cluster)")]

    %% Queues
    subgraph Queues [Message Queues]
        Q_Geo[("Geo<br/>Queue")]
        Q_Ping[("Ping<br/>Queue")]
        Q_Rdap[("RDAP<br/>Queue")]
        Q_Rdns[("Rev DNS<br/>Queue")]
    end

    %% Worker Pools
    subgraph Workers [Worker Pools]
        W_Geo["Geo Workers<br/>(x5)"]
        W_Ping["Ping Workers<br/>(x3)"]
        W_Rdap["RDAP Workers<br/>(x2)"]
        W_Rdns["RDNS Workers<br/>(x2)"]
    end

    %% Routing Connections
    LB --> API1
    LB --> API2
    LB --> API3

    %% API to Broker
    API1 & API2 & API3 --> RMQ

    %% Broker to Queues
    RMQ --> Q_Geo
    RMQ --> Q_Ping
    RMQ --> Q_Rdap
    RMQ --> Q_Rdns

    %% Queues to Workers
    Q_Geo --> W_Geo
    Q_Ping --> W_Ping
    Q_Rdap --> W_Rdap
    Q_Rdns --> W_Rdns

    %% Styling
    style LB fill:#fff,stroke:#333,stroke-width:2px
    style RMQ fill:#f9f,stroke:#333,stroke-width:2px
    
    classDef queue fill:#eee,stroke:#333,stroke-width:2px,stroke-dasharray: 5 5;
    class Q_Geo,Q_Ping,Q_Rdap,Q_Rdns queue;
    
    classDef cluster fill,stroke:#01579b;
    class API1,API2,API3,W_Geo,W_Ping,W_Rdap,W_Rdns cluster;
```

## 11. Failure Scenarios

### Scenario A: Worker Crashes Mid-Process

```mermaid
flowchart TD
  A[Worker crashes] --> B[Message not ACKed]
  B --> C[RabbitMQ redelivers]
  C --> D[Another worker picks up]
  D --> E[Job completes]

  %% Minimal, high-contrast emphasis (only where it adds clarity)
  classDef alert fill:#7a0000,stroke:#2b0000,color:#ffffff,stroke-width:2px;
  classDef success fill:#0b5d1e,stroke:#04340f,color:#ffffff,stroke-width:2px;

  class B alert
  class E success

```

### Scenario B: External API Times Out

```mermaid
flowchart TD
    %% Nodes
    Timeout(External API timeout)
    Catch[Worker catches exception]
    
    Publish[Publishes TaskCompleted<br/>Success = false]
    
    Saga[Saga updates job with error]
    
    Continue[Other tasks continue<br/>processing parallelly]
    
    Result([Job completes<br/>partial success])

    %% Connections
    Timeout -.->|Error| Catch
    Catch --> Publish
    Publish --> Saga
    Saga --> Continue
    Continue --> Result

    %% Styling
    classDef error fill:#7a0000,stroke:#721c24,stroke-width:2px;
    classDef warning fill:#fff3cd,stroke:#856404,stroke-width:2px;
    
    class Timeout,Catch,Publish error;
    class Result warning;
```

### Scenario C: Redis Crashes

```mermaid
flowchart TD
    %% Nodes
    Crash(Redis crashes)
    ApiError[API can't save jobs<br/>503 Error]
    Restart(Redis restarts)
    DataLoss[In-flight jobs lost<br/>Acceptable for MVP]
    Recovery([New jobs work fine])

    %% Flow
    Crash -->|Connection Failure| ApiError
    
    ApiError -.->|System Recovery| Restart
    
    Restart --> DataLoss
    DataLoss --> Recovery

    %% Styling
    classDef failure fill:#7a0000,stroke:#721c24,stroke-width:2px;
    classDef warning fill:#fff3cd,stroke:#856404,stroke-width:2px;
    classDef success fill:#d4edda,stroke:#155724,stroke-width:2px;

    class Crash,ApiError failure;
    class DataLoss warning;
    class Recovery success;
```

## 12. Extension Points

```mermaid
flowchart LR
    %% Main Graph Direction: Left to Right (Comparison)

    %% --- CURRENT STATE (Left Column) ---
    subgraph Current [Current State]
        direction TB
        
        %% Communication
        C_Comm["HTTP Poll"]
        
        %% Storage
        C_Data["Redis<br/>(Volatile)"]
        
        %% Infra
        C_Infra["Single Instance<br/>[API | Workers]"]
        
        %% Layout invisible links to stack them vertically
        C_Comm ~~~ C_Data ~~~ C_Infra
    end

    %% --- TRANSITION ARROW ---
    Current ==>|Evolution| Future

    %% --- FUTURE STATE (Right Column) ---
    subgraph Future [Future Enhancement]
        direction TB
        
        %% Communication
        subgraph F_Comm_Grp [Real-time]
            direction TB
            F_Poll[HTTP Poll]
            F_Socket[WebSocket<br/>Push]
            F_Poll -.-> F_Socket
        end

        %% Storage
        subgraph F_Data_Grp [Persistence]
            direction TB
            F_Db[("PostgreSQL<br/>(Durable)")]
            F_Cache[("Redis<br/>(Cache)")]
            F_Db --> F_Cache
        end
        
        %% Infra
        subgraph F_K8s [Kubernetes Cluster]
            direction TB
            F_Pods["API Pods (x10)<br/>Worker Pods (x50)"]
            F_Clust["Redis Cluster<br/>RabbitMQ Cluster"]
            F_Pods --- F_Clust
        end

        %% Layout invisible links
        F_Comm_Grp ~~~ F_Data_Grp ~~~ F_K8s
    end

    %% --- STYLING ---
    %% Amber for Current (Limited/Fragile)
    classDef current fill:#fff3cd,stroke:#856404,stroke-width:2px;
    class C_Comm,C_Data,C_Infra current;

    %% Green for Future (Robust/Scalable)
    classDef future fill:#d4edda,stroke:#155724,stroke-width:2px;
    class F_Poll,F_Socket,F_Db,F_Cache,F_Pods,F_Clust future;
    
    %% Subgraph styling
    classDef group fill:white,stroke:#ccc,stroke-dasharray: 5 5;
    class F_Comm_Grp,F_Data_Grp,F_K8s group;
```

## 13. Monitoring Points

```
┌─────────────────────────────────────────────┐
│              Metrics to Track               │
├─────────────────────────────────────────────┤
│ API:                                        │
│  - Requests/sec                             │
│  - P50/P95/P99 latency                      │
│  - Error rate                               │
│                                             │
│ RabbitMQ:                                   │
│  - Queue depth                              │
│  - Message rate (in/out)                    │
│  - Consumer count                           │
│                                             │
│ Workers:                                    │
│  - Processing time per service              │
│  - Success/failure rate                     │
│  - Active worker count                      │
│  - Storage backend latency                │
│                                             │
│ Redis:                                      │
│  - Hit rate                                 │
│  - Memory usage                             │
│  - Key count                                │
│                                             │
│ Saga:                                       │
│  - Jobs in flight                           │
│  - Average completion time                  │
│  - State transition errors                  │
│                                             │
│ Storage Layer:                            │
│  - Write latency (p50/p95/p99)              │
│  - Backend distribution (Redis/S3/etc)      │
│  - ResultLocation types in use              │
└─────────────────────────────────────────────┘
```

---

## Architecture Highlights 

### Template Method Pattern Benefits

**Before (Each worker ~150 lines):**
- Duplicated timing code
- Duplicated validation
- Duplicated persistence logic
- Duplicated error handling
- Inconsistent patterns

**After (Each worker ~30 lines):**
- Single source of truth
- Guaranteed consistency
- Trivial to add new services
- 90% code reduction

### Storage Abstraction Benefits

**Current State:**
- Redis for all results
- Fast but limited by memory
- Single storage type

**Future Ready:**
- Small results → Redis (fast)
- Large results → S3 (cheap)
- Structured data → DynamoDB
- No worker code changes needed

---

These diagrams illustrate the **distributed, asynchronous, fault-tolerant** nature of the system. Each component is independently scalable and resilient to failures. The new worker base class pattern and storage abstraction layer demonstrate production-ready design patterns that eliminate duplication and enable future extensibility.