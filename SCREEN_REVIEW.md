# ZenConnect

ZenConnect is a separate screenshot feedback module. Each desktop creates its
own room key on first use. The key is stored at
`%LocalAppData%\ZenConnect\device-key.txt` and reused on later runs.

## Start the server

Install the .NET 8 SDK, then run PowerShell from the repository root:

```powershell
dotnet run --project .\ScreenReviewServer\ScreenReviewServer.csproj
```

Start ZenConnect and press `KET NOI`. The desktop registers its key as a room.
Use the `COPY` button in the desktop window, open `http://localhost:5080`, and
enter that key to view the room. Screenshots and messages are isolated by key.

## Deploy to Render

The repository root contains `render.yaml`, and the server has its own
`Dockerfile`. Push the repository to GitHub or GitLab, then create a Render
Blueprint from that repository. No shared room or access-key environment
variable is required.

The Blueprint uses a persistent disk mounted at `/var/data`, preserving room
registrations, screenshots, and messages across restarts and deploys. After
deployment, put the service URL, such as
`https://screen-review-server.onrender.com`, in the desktop Server URL field.

## Desktop client

Build and start `RunToolSrc`, then open `ZENCONNECT`. On the first run, the
desktop generates a key in the format `XXXX-XXXX-XXXX-XXXX-XXXX`. Deleting the
local key file creates a new room on the next run; the old room remains on the
server until its stored data is removed.
