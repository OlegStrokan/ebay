# Gabriela

This is my OWN description. Tech guy explain things to another tech guy. if you want clean ass readme - check README.md file. 

--------------------------------------------
just check order service. it's cool as fuck. like 8/10 on my opinion

## ebay:

### user service

user service probably should be a rest api, but 90% of other microservices use grpc and don't expose any methods for public, all use gateway, so to make it consistent
i prefer consistency to logic. user-service will be grpc microservice, yes it's overkill, but zhizn' igra, igray krasivo

### auth service

some auth stuff. complexity same as user.  simple af. depends on user

### order service:
saga based transaction with sprinkle of outbox transactions with stop/resume saga transaction, fully compensatable steps with escalation to jira or help desk, kafka workers, dlq, partition stopping/resuming, ddd aggregates, distributed locking, watchdogs, event sourcing, snapshots, projection models, cqrs, and ALMOST exactly-once processing. also support b2b orders and subscription (recurring order). supraphysiological amount of lean unit tests, also some integration and e2e tests type shit. e2e very cool, sir. 

### payment service:
kinda complex. use stripe api, handle dual path finalization: sync (stripe return response), async (webhook push + reconciliation pull for pending/require-shit payments). something like 'effectively-once' semantic: at-least-once delivery + idempotency + db constraints + outbox + reconciliation

### product service: 
internal kafka cqrs service. used by order (kafka)

### product-admin service:
REST APIcko for product-guy

### inventory service:
simple. saga participant. serializable reservation. nothing cool about this service. junior level shit

### catalog service: 
consumes product events from kafka and write it to elasticsarc. not so much

### email-service: 
simple email + idempotency, DLQ, replay worker and shit. so junior+ level

### search service:
search items in 2 ways:
- without AI => plain elasticsearch
- with AI => call pythonAI service, falls back to elastic on timeout/error

### gateway: 
rest api bottle cap. mick swagger, routing, simple

AI: 

### ai-search-service: 
- receive request, start ai pipeline, make 2 parallel calls to ai and elastic, merge and return result
- directly call llm-query-service and embedding-service
- parser for llm: transform user prompt into structured data (what user means)

### vector-indexer-worker:
- consume product events from kafka and upsert into Qdrant 

### embedding-service
- bridge between LLM and eshop microservices (what vector represents of what user means)

