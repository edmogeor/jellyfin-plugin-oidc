"""Complete Jellyfin setup and configure the plugin for the local OIDC test realm."""

import json
import time
import urllib.error
import urllib.request

BASE = "http://jellyfin-server:8096"
PLUGIN_ID = "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4"


def request(method, path, body=None, token=None):
    headers = {"Content-Type": "application/json"}
    headers["Authorization"] = (
        f'MediaBrowser Token="{token}"' if token else 'MediaBrowser Client="e2e", Device="e2e", DeviceId="e2e", Version="1"'
    )
    data = json.dumps(body).encode() if body is not None else None
    try:
        with urllib.request.urlopen(urllib.request.Request(BASE + path, data=data, headers=headers, method=method), timeout=5) as response:
            return response.status, json.loads(response.read() or b"null")
    except (urllib.error.URLError, urllib.error.HTTPError) as error:
        return getattr(error, "code", 0), None


for _ in range(90):
    status, info = request("GET", "/System/Info/Public")
    if status == 200:
        break
    time.sleep(2)
else:
    raise SystemExit("Jellyfin did not start")

if not info.get("StartupWizardCompleted", False):
    request("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
    request("GET", "/Startup/User")
    request("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False})
    request("POST", "/Startup/Complete")

for _ in range(30):
    status, result = request("POST", "/Users/AuthenticateByName", {"Username": "root", "Pw": ""})
    if status == 200:
        break
    time.sleep(2)
else:
    raise SystemExit("Could not authenticate as Jellyfin root")

token = result["AccessToken"]
status, configuration = request("GET", f"/Plugins/{PLUGIN_ID}/Configuration", token=token)
if status != 200:
    raise SystemExit("OIDC plugin configuration is unavailable")

configuration.update(
    {
        "Enabled": True,
        "PublicUrl": "https://localhost:8443",
        "IssuerUrl": "https://oidc.localhost:8443/keycloak/realms/jellyfin",
        "ClientId": "jellyfin",
        "ClientSecret": "oidc-test-secret",
        "GroupClaim": "groups",
        "UserGroup": "jellyfin-users",
        "AdministratorGroup": "jellyfin-admins",
        "LoginButtonText": "Login with SSO",
        "PasswordLoginMode": "AllowForAllUsers",
        "RpInitiatedLogout": False,
    }
)
status, _ = request("POST", f"/Plugins/{PLUGIN_ID}/Configuration", configuration, token)
if status not in (200, 204):
    raise SystemExit("Could not configure the OIDC plugin")

print("Jellyfin and OIDC test realm configured")
