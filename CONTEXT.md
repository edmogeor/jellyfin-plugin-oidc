# Jellyfin OIDC Authentication

This plugin lets Jellyfin users authenticate through one OpenID Connect provider. It targets Jellyfin 12.

## Language

**Jellyfin User**:
A local Jellyfin account that owns its permissions and media activity.
_Avoid_: Account

**Identity Provider**:
The OpenID Connect service that authenticates a user.
_Avoid_: SSO provider, OIDC provider

**Provisioning**:
Creating a Jellyfin User when an eligible Identity Provider identity has no email match.

**Allowed Group**:
An Identity Provider group permitted to sign in and provision Jellyfin Users.

**Administrator Group**:
An Allowed Group whose members are Jellyfin administrators.

**Email Match**:
A case-insensitive match between a verified Identity Provider email and a Jellyfin User username.

**Identity Link**:
A binding between an Identity Provider subject and a Jellyfin User.

## Relationships

- An **Identity Provider** authenticates a **Jellyfin User**.
- An eligible **Identity Provider** identity may be **Provisioned** as a **Jellyfin User**.
- An **Allowed Group** permits an Identity Provider identity to access Jellyfin.
- An **Administrator Group** grants administrator access to its Jellyfin Users.
- An **Email Match** identifies the Jellyfin User for an Identity Provider identity.
- An **Identity Link** identifies a returning Identity Provider identity.

## Example dialogue

> **Dev:** "Can an Identity Provider identity sign in as an existing Jellyfin User?"
> **Domain expert:** "Yes, once it is safely matched to that user."
