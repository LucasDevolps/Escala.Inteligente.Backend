# Schedule Manager — Backend

Backend do sistema de gerenciamento de escalas, em .NET 10, ASP.NET Core, EF Core/SQL Server, SignalR e RabbitMQ. A solução usa Clean Architecture com monólito modular e pode ser versionada como repositório independente.

## Estrutura

```text
src/
  ScheduleManager.Domain          entidades, enums e invariantes
  ScheduleManager.Application     contratos, casos de uso e motor determinístico
  ScheduleManager.Infrastructure  EF Core, SQL Server, JWT, AES-GCM, RabbitMQ e OTel
  ScheduleManager.Api             HTTP, policies, Problem Details e SignalR
  ScheduleManager.Worker          Outbox, Inbox, retries/DLQ e retenção
tests/
  ScheduleManager.UnitTests
  ScheduleManager.IntegrationTests
  ScheduleManager.ArchitectureTests
```

## Pré-requisitos

- .NET SDK 10.0.301 ou patch compatível;
- Docker com Compose;
- certificado HTTPS de desenvolvimento confiável (`dotnet dev-certs https --trust`).

## Subir SQL Server e RabbitMQ

Nenhuma senha é versionada. Defina os três valores no ambiente ou crie um `.env` local a partir de `.env.example`:

```powershell
$env:SQL_SA_PASSWORD='<senha forte aceita pelo SQL Server>'
$env:RABBITMQ_USER='<usuario local>'
$env:RABBITMQ_PASSWORD='<senha local forte>'
docker compose up -d --wait
```

As portas são expostas somente em loopback: SQL Server `1433`, AMQP `5672` e console RabbitMQ `15672`. Em caso de conflito local, altere apenas as portas do host com `SQL_SERVER_PORT`, `RABBITMQ_AMQP_PORT` e `RABBITMQ_MANAGEMENT_PORT`.

## Configuração local e bootstrap

Gere dois segredos independentes de 32 bytes em Base64:

```powershell
$jwtBytes = [byte[]]::new(32)
$aesBytes = [byte[]]::new(32)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($jwtBytes)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($aesBytes)
$jwtKey = [Convert]::ToBase64String($jwtBytes)
$aesKey = [Convert]::ToBase64String($aesBytes)
```

Configure API e Worker pelo ambiente. O bootstrap é idempotente e não possui credencial padrão:

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:DOTNET_ENVIRONMENT='Development'
$env:ConnectionStrings__DefaultConnection="Server=localhost,1433;Database=ScheduleManager;User Id=sa;Password=$env:SQL_SA_PASSWORD;Encrypt=True;TrustServerCertificate=True"
$env:Jwt__Issuer='ScheduleManager'
$env:Jwt__Audience='ScheduleManager.Web'
$env:Jwt__SigningKeyBase64=$jwtKey
$env:Encryption__KeyId='dev-2026-01'
$env:Encryption__KeyBase64=$aesKey
$env:RabbitMq__HostName='localhost'
$env:RabbitMq__UserName=$env:RABBITMQ_USER
$env:RabbitMq__Password=$env:RABBITMQ_PASSWORD
$env:RabbitMq__UseTls='false'
$env:Database__MigrateOnStartup='true'
$env:Bootstrap__Enabled='true'
$env:Bootstrap__OrganizationName='<nome da organizacao>'
$env:Bootstrap__TimeZoneId='America/Sao_Paulo'
$env:Bootstrap__ManagerName='<nome do gestor>'
$env:Bootstrap__ManagerEmail='<email do gestor>'
$env:Bootstrap__ManagerPhone='<telefone do gestor>'
$env:Bootstrap__ManagerPassword='<frase-senha com 12 a 128 caracteres>'
```

Execute em terminais separados:

```powershell
dotnet run --project src/ScheduleManager.Api --launch-profile https
dotnet run --project src/ScheduleManager.Worker
```

- API: `https://localhost:7212`
- OpenAPI em Development: `https://localhost:7212/openapi/v1.json`
- Hub SignalR: `/hubs/notifications`

Após o primeiro seed, `Bootstrap__Enabled` pode ser desativado. O código de ativação de cada colaborador é retornado uma única vez por `POST /api/v1/employees`.

Em `Development`, a carga de referência de julho de 2026 procura Miriam e Eli já cadastrados na
mesma organização e cria somente a escala e suas atribuições. A carga é idempotente, não cria
funcionários e pode ser desativada com `ReferenceScheduleSeed__Enabled=false`. Para executá-la
manualmente em uma base local, use `scripts/seed-july-2026-reference-schedule.sql`.

## Autenticação e navegador

- Access token JWT: cinco minutos, somente em memória no frontend;
- refresh token: cookie `HttpOnly`, `Secure`, `SameSite=Strict`, nunca persistido em claro;
- XSRF: cookie `XSRF-TOKEN` no path `/` e header `X-XSRF-TOKEN` em refresh/logout;
- uma sessão ativa por usuário; transação serializável e índice filtrado único fazem o novo login revogar/substituir a anterior mesmo sob concorrência;
- refresh rotativo, histórico persistido e revogação da família em replay;
- tentativas inválidas de login são acumuladas com `rowversion` e retry serializável, sem lost update no lockout;
- após mais de cinco minutos sem refresh válido, a sessão deixa de ser renovável.

O login não recebe organização. Para eliminar ambiguidade, o MVP reforça `NormalizedEmail` como único globalmente, além do índice composto exigido por organização.

## Decisões do motor

O motor é puro e determinístico. O alvo diário do MVP é `MinEmployeesPerDay`; `MaxEmployeesPerDay` é o teto para edição manual. Quando a cobertura mínima é impossível sem quebrar uma hard constraint, o motor conserva o resultado parcial e gera `MINIMUM_COVERAGE_UNAVAILABLE`. Produtividade é apenas um desempate limitado, depois de equilíbrio, consecutividade, finais de semana e histórico.

Colaboradores recebem, em consultas de escala publicada, somente seus próprios assignments; produtividade e alertas gerenciais não são expostos.

## Mensageria

- alterações e Outbox são gravados na mesma transação SQL;
- o Worker publica mensagens mínimas, sem conteúdo textual pessoal;
- Inbox possui chave composta `(MessageId, ConsumerName)`;
- consumer: tentativa inicial, 5 s, 30 s e 2 min; depois registra `ApplicationError` e envia a entrega original a `<queue>.dead` via NACK/DLX atômico do RabbitMQ;
- publicação do Outbox usa backoff persistente 5 s/30 s/2 min. DLQ só se aplica ao consumer, pois uma mensagem ainda não publicada não pode ser enviada legitimamente à DLQ;
- texto de Notification é AES-256-GCM com nonce único e AAD formado por notification/organization/recipient/type.

A API emite SignalR imediatamente após o commit; o Worker mantém o caminho durável/idempotente. Em implantação com múltiplas instâncias de API, deve-se configurar um backplane SignalR compatível.

## Produção

- forneça SQL, JWT, AES e RabbitMQ por Secret Manager/Key Vault ou variáveis protegidas;
- `RabbitMq__UseTls=true` é obrigatório fora de Development; AMQPS sem TLS falha no startup;
- configure `Cors__AllowedOrigins__0`, `Cors__AllowedOrigins__1`, etc. com allowlist explícita;
- use conexão SQL com criptografia e certificado validado;
- configure `OpenTelemetry__OtlpEndpoint` quando houver collector;
- chaves AES anteriores podem ser disponibilizadas em `Encryption__PreviousKeys__<keyId>` durante rotação;
- retenções devem ser positivas. Defaults: Notifications 180 dias, ApplicationErrors 180 dias e sessões revogadas 90 dias. AuditLogs e histórico de escalas não são apagados automaticamente.
- logs HTTP de framework ficam em `Warning` por padrão, evitando registrar a query string `access_token` usada pelo transporte WebSocket do SignalR.

## Build e testes

```powershell
dotnet restore ScheduleManager.slnx
dotnet tool restore
dotnet build ScheduleManager.slnx --no-restore
dotnet test ScheduleManager.slnx --no-build
```

Os testes de integração iniciam SQL Server real via Testcontainers; Docker precisa estar disponível. O build trata warnings, inclusive alertas de vulnerabilidade NuGet, como erro.
