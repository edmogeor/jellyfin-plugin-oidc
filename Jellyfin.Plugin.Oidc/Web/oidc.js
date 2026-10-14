(() => {
    const oidcUrl = new URL('.', document.currentScript.src);
    const serverUrl = new URL('../', oidcUrl);
    const iconVersion = new URL(document.currentScript.src).search;
    const providerNames = { Authentik: 'Authentik', Authelia: 'Authelia', Keycloak: 'Keycloak', PocketId: 'Pocket ID', Zitadel: 'ZITADEL' };
    const providerName = (provider = window.oidcProvider, name = window.oidcProviderName) => Object.hasOwn(providerNames, provider) ? providerNames[provider] : name?.trim() || profileStrings.linkHeading;
    const endpoint = () => {
        const route = new URL(location.hash.replace(/^#!?/, '') || '/home', location.origin);
        const destination = route.pathname === '/login' ? route.searchParams.get('url') || '/home' : route.pathname + route.search;
        const remember = document.querySelector('form.manualLoginForm input[type="checkbox"]')?.checked ?? localStorage.getItem('enableAutoLogin') !== 'false';
        return oidcUrl + 'start?returnUrl=' + encodeURIComponent(destination) + '&rememberMe=' + remember;
    };
    const isLoginPage = () => /^#!?\/login(?:\?|$)/.test(location.hash);
    const hasOidcError = () => location.search.includes('oidcError=1') || location.hash.includes('oidcError=1');
    const isSignedOut = () => location.search.includes('oidcSignedOut=1');
    const loginLabel = () => profileStrings.signInWithProvider.replace('{0}', () => providerName());
    const signedOutLabel = () => window.oidcSignedOutText || 'Signed Out';
    const revealLogin = () => document.querySelector('#oidc-login-redirect-style')?.remove();
    const isPrimaryLogin = () => window.oidcPasswordLoginMode === 'DisableForAllUsers';
    let shownError;
    let redirectingToProvider;
    const display = (elements, value) => elements.filter(Boolean).forEach(element => value ? element.style.setProperty('display', value, 'important') : element.style.removeProperty('display'));
    const localControls = form => [
        ...form.querySelectorAll(':scope > .inputContainer, :scope > button[type="submit"]'),
        form.querySelector('.btnCancel')?.parentElement,
    ];
    const loadStrings = async () => {
        const locale = document.documentElement.lang.toLowerCase();
        for (const value of new Set([locale, locale.split('-')[0], 'en-us'])) {
            try {
                const response = await fetch(oidcUrl + 'strings/' + encodeURIComponent(value));
                if (response.ok) return await response.json();
            } catch { /* Embedded English remains available when translation requests fail. */ }
        }
        return {};
    };
    const profileStrings = /* @english-strings */ {};
    const uiStyle = document.createElement('style');
    uiStyle.textContent = '.formDialogHeader.justify-content-center > .formDialogHeaderTitle { margin-inline: 0; } #oidc-admin-panel .emby-button .material-icons { font-size: 1.2em; margin-inline-end: 0.4em; } #oidc-admin-panel button:disabled, #oidc-admin-panel button:disabled:hover { background: var(--jf-palette-action-disabledBackground); color: var(--jf-palette-text-disabled); opacity: 0.5; cursor: not-allowed; box-shadow: none; transform: none; } .oidc-login-content { display: flex; align-items: center; justify-content: center; gap: 0.5em; max-width: 100%; } [data-oidc-login-label], [data-oidc-profile-name], [data-oidc-admin-provider-name] { min-width: 0; overflow-wrap: anywhere; } .oidc-provider-icon { display: inline-block; width: 1.25em; height: 1.25em; flex-shrink: 0; color: inherit; } .oidc-provider-icon:not(.material-icons) { background-color: currentColor; } .oidc-provider-icon.material-icons { font-size: 1.25em; width: 1em; height: 1em; line-height: 1; } .oidc-admin-status-line { display: flex; align-items: center; flex-wrap: wrap; gap: 0.75em; } .oidc-admin-provider { display: inline-flex; align-items: center; gap: 0.5em; min-width: 0; max-width: 100%; padding-inline-end: 0.75em; border-inline-end: 1px solid var(--jf-palette-divider); color: var(--jf-palette-text-primary, inherit); }';
    document.head.append(uiStyle);
    function updateLoginPresentation(button) {
        const text = loginLabel();
        button.setAttribute('aria-label', text);
        button.querySelector('[data-oidc-login-label]').textContent = text;
        updateProviderIcon(button.querySelector('[data-oidc-provider-icon]'));
    }
    function updateProviderIcon(icon, provider = window.oidcProvider) {
        provider = Object.hasOwn(providerNames, provider) ? provider : 'Other';
        if (icon.dataset.oidcProviderIcon === provider) return;
        icon.dataset.oidcProviderIcon = provider;
        icon.className = 'oidc-provider-icon' + (provider === 'Other' ? ' material-icons' : '');
        icon.textContent = provider === 'Other' ? 'vpn_key' : '';
        const mask = provider === 'Other' ? '' : `url("${oidcUrl}icons/${provider}.svg${iconVersion}") center / contain no-repeat`;
        icon.style.mask = mask;
        icon.style.webkitMask = mask;
    }
    function linkedIdentityText(state) {
        const identity = state.Email || state.Username;
        return identity ? profileStrings.linkLinkedAs.replace('{0}', identity) : profileStrings.linkLinked;
    }
    const statusIconStyle = { fontSize: '1.1em', verticalAlign: 'middle', marginInlineEnd: '0.3em' };
    function identityStatus(state) {
        return {
            text: state.Linked ? linkedIdentityText(state) : state.Request ? profileStrings.adminPending : profileStrings.linkNotLinked,
            icon: state.Linked ? 'check' : state.Request ? 'schedule' : 'link_off',
            color: state.Linked ? 'var(--jf-palette-success-main, inherit)' : undefined,
        };
    }
    function approvalCriterion(request) {
        if (request.Subject) return 'Subject';
        if (request.Username) return 'Username';
        return 'Email';
    }
    const confirmUnlink = async (state, self) => {
        let passwordWarning = 'adminPasswordAllowed';
        if (state.PasswordLoginMode === 'DisableForAllUsers') passwordWarning = 'adminPasswordDisabled';
        else if (state.PasswordLoginMode === 'DisableForLinkedUsersOnly') passwordWarning = 'adminPasswordRestored';
        const warnings = [profileStrings.adminUnlinkWarning.replace('{0}', state.Name || ''), profileStrings[passwordWarning]];
        if (self) warnings.push(profileStrings.adminSelfWarning);
        if (state.SoleAdministrator) warnings.push(profileStrings.adminSoleWarning);
        const escape = value => { const node = document.createElement('span'); node.textContent = value; return node.innerHTML; };
        const annotateDialog = new MutationObserver(() => {
            const dialog = document.getElementById('oidc-unlink-confirm');
            if (!dialog) return;
            dialog.setAttribute('role', 'dialog'); dialog.setAttribute('aria-modal', 'true'); dialog.setAttribute('aria-label', profileStrings.adminUnlink);
            const description = dialog.querySelector('.text');
            if (description) { description.id = 'oidc-unlink-confirm-description'; dialog.setAttribute('aria-describedby', description.id); }
            annotateDialog.disconnect();
        });
        annotateDialog.observe(document.body, { childList: true, subtree: true });
        try {
            // Use the same Jellyfin confirmation API as the configuration page.
            return await new Promise(resolve => window.Dashboard.confirm({ title: profileStrings.adminUnlink, text: warnings.map(value => '<p>' + escape(value) + '</p>').join(''), primary: 'delete', confirmText: profileStrings.adminUnlink, dialogOptions: { id: 'oidc-unlink-confirm' } }, profileStrings.adminUnlink, resolve));
        } finally { annotateDialog.disconnect(); }
    };
    const returnToSignIn = session => {
        const credentials = savedCredentials();
        for (const server of credentials.Servers || []) if (server.AccessToken === session.token) { server.AccessToken = null; server.UserId = null; server.ExchangeToken = null; }
        localStorage.setItem('jellyfin_credentials', JSON.stringify(credentials));
        location.assign(serverUrl + 'web/index.html?oidcSignedOut=1#!/login');
    };
    const savedCredentials = () => {
        try { return JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}') || {}; }
        catch { return {}; }
    };
    const activeSession = () => {
        const client = window.ApiClient;
        if (!client?.accessToken() || !client.getCurrentUserId()) return null;
        const address = new URL(client.serverAddress(), location.href);
        if (address.origin !== serverUrl.origin || address.pathname.replace(/\/$/, '') !== serverUrl.pathname.replace(/\/$/, '')) return null;
        return { userId: client.getCurrentUserId(), token: client.accessToken() };
    };
    const profileRoute = () => {
        const route = location.hash.replace(/^#!?/, '');
        const url = new URL(route, location.origin);
        return url.pathname === '/userprofile' ? url : null;
    };
    const linkRequest = (session, path, options = {}) => fetch(oidcUrl + path, {
        ...options, cache: 'no-store', headers: { Authorization: `MediaBrowser Token="${session.token}"`, 'Content-Type': 'application/json' },
    });
    function requestCurrentUser(token) {
        return fetch(serverUrl + 'Users/Me', { headers: { Authorization: `MediaBrowser Token="${token}"` } });
    }
    let nativeLoginReady;
    let installingSession;
    document.addEventListener('viewshow', event => {
        if (event.target.id === 'loginPage') { nativeLoginReady = true; void installSession(); queueMicrotask(showError); }
        if (window.oidcHandoff) queueMicrotask(() => { if (window.oidcHandoff) window.Dashboard?.showLoadingMsg(); });
    }, true);
    async function installSession() {
        const handoff = window.oidcHandoff;
        const client = window.ApiClient;
        if (!handoff || !nativeLoginReady || installingSession
            || typeof client?.onAuthenticated !== 'function' || !window.Dashboard?.onServerChanged) return;
        const address = new URL(client.serverAddress(), location.href);
        if (address.origin !== serverUrl.origin || address.pathname.replace(/\/$/, '') !== serverUrl.pathname.replace(/\/$/, '')) return;
        installingSession = true;
        window.Dashboard.showLoadingMsg();
        try {
            const response = await fetch(serverUrl + 'Users/Me', { signal: AbortSignal.timeout(10000), headers: { Authorization: `MediaBrowser Token="${handoff.session.accessToken}"` } });
            if (!response.ok) throw new Error('Session handoff failed');
            const user = await response.json();
            await client.onAuthenticated(client, { User: user, ServerId: handoff.session.user.serverId, AccessToken: handoff.session.accessToken });
            window.Dashboard.onServerChanged(user.Id, handoff.session.accessToken, client);
            localStorage.setItem('enableAutoLogin', String(handoff.rememberMe));
            window.oidcHandoff = null;
            await window.Dashboard.navigate(handoff.returnUrl);
        } catch {
            window.oidcHandoff = null;
            location.hash = '!/login?oidcError=1';
            revealLogin();
        } finally {
            document.querySelector('#oidc-session-loading-style')?.remove();
            window.Dashboard.hideLoadingMsg();
        }
    }
    function sameUser(first, second) {
        return first.replaceAll('-', '').toLowerCase() === second.replaceAll('-', '').toLowerCase();
    }
    const linkNotification = (text, error = false, duration = 5000) => {
        const container = document.querySelector('.toastContainer') || document.body.appendChild(document.createElement('div'));
        container.classList.add('toastContainer');
        const message = document.createElement('div');
        message.className = 'toast'; message.textContent = text; message.setAttribute('role', error ? 'alert' : 'status');
        container.append(message);
        setTimeout(() => message.classList.add('toastVisible'), 300);
        setTimeout(() => message.remove(), duration);
        return message;
    };
    let profileSection;
    let profileUnavailableKey;
    let completingLink;
    const restoreProfilePasswords = () => document.querySelectorAll('[data-oidc-password-hidden]').forEach(form => {
        form.removeAttribute('data-oidc-password-hidden'); display([form]);
    });
    function setProfilePasswordVisibility(content, disabled) {
        if (disabled === undefined) return;
        const form = content.querySelector('.updatePasswordForm');
        if (!form) return;
        if (disabled) { form.dataset.oidcPasswordHidden = 'true'; display([form], 'none'); }
        else if (form.hasAttribute('data-oidc-password-hidden')) { form.removeAttribute('data-oidc-password-hidden'); display([form]); }
    }
    function updateProfile() {
        const session = activeSession();
        const route = profileRoute();
        const requestedUser = route?.searchParams.get('userId');
        const content = document.querySelector('#userProfilePage .readOnlyContent')?.parentElement;
        if (!session || !route || !content || (requestedUser && !sameUser(requestedUser, session.userId))) {
            profileSection?.remove(); profileSection = null;
            restoreProfilePasswords();
            return;
        }
        const key = session.userId + ':' + session.token;
        if (profileUnavailableKey === key) return;
        if (profileSection?.isConnected && profileSection.sessionKey === key) {
            setProfilePasswordVisibility(content, profileSection.passwordLoginDisabled);
            return;
        }
        profileSection?.remove();
        restoreProfilePasswords();
        mountProfile(content, session, route, key);
    }
    function createProfileCard(content, key) {
        const section = document.createElement('section');
        profileSection = section; section.sessionKey = key;
        section.dataset.oidcProfile = 'true'; section.className = 'readOnlyContent detailSection';
        section.style.margin = '0 auto 2em';
        const card = document.createElement('div'); card.className = 'paperList';
        const body = document.createElement('div'); body.className = 'listItemBody';
        Object.assign(body.style, { display: 'flex', flexDirection: 'row', alignItems: 'center', flexWrap: 'wrap', gap: '1rem', padding: '1em', boxSizing: 'border-box', width: '100%' });
        const details = document.createElement('div'); Object.assign(details.style, { flex: '1 1 14rem', minWidth: '0' });
        const heading = document.createElement('div'); heading.className = 'listItemBodyText';
        const headingIcon = document.createElement('span'); headingIcon.dataset.oidcProviderIcon = ''; headingIcon.setAttribute('aria-hidden', 'true');
        const headingName = document.createElement('span'); headingName.dataset.oidcProfileName = 'true'; headingName.textContent = providerName('Other', '');
        updateProviderIcon(headingIcon, 'Other');
        heading.append(headingIcon, headingName);
        heading.setAttribute('role', 'heading'); heading.setAttribute('aria-level', '2');
        Object.assign(heading.style, { display: 'flex', alignItems: 'center', gap: '0.5em', whiteSpace: 'normal', fontWeight: '600', color: 'var(--jf-palette-text-primary, inherit)' });
        const status = document.createElement('p'); status.className = 'listItemBodyText secondaryText'; status.setAttribute('role', 'status');
        Object.assign(status.style, { overflowWrap: 'anywhere', margin: '0' });
        const icon = document.createElement('span'); icon.className = 'material-icons hide'; icon.setAttribute('aria-hidden', 'true');
        Object.assign(icon.style, statusIconStyle);
        const statusText = document.createElement('span'); statusText.dataset.oidcStatusText = 'true'; status.append(icon, statusText);
        const setStatus = (text, presentation = null) => {
            statusText.textContent = text;
            icon.classList.toggle('hide', presentation === null);
            icon.textContent = presentation?.icon || '';
            if (presentation?.color) status.style.color = presentation.color;
            else status.style.removeProperty('color');
        };
        setStatus(profileStrings.linkChecking);
        const help = document.createElement('p'); help.className = 'fieldDescription hide';
        const actions = document.createElement('div'); actions.style.marginInlineStart = 'auto';
        const button = document.createElement('button'); button.type = 'button'; button.setAttribute('is', 'emby-button'); button.className = 'emby-button raised hide'; button.style.margin = '0';
        const showButton = visible => button.classList.toggle('hide', !visible);
        const buttonIcon = document.createElement('span'); buttonIcon.className = 'material-icons'; buttonIcon.textContent = 'link'; buttonIcon.setAttribute('aria-hidden', 'true');
        Object.assign(buttonIcon.style, { fontSize: '1.2em', marginInlineEnd: '0.4em' });
        const label = document.createElement('span'); button.append(buttonIcon, label);
        details.append(heading, status, help); actions.append(button); body.append(details, actions); card.append(body); section.append(card);
        let anchor = content.querySelector('.updatePasswordForm');
        while (anchor && anchor.parentElement !== content) anchor = anchor.parentElement;
        content.insertBefore(section, anchor);
        return { section, headingIcon, headingName, help, button, buttonIcon, label, setStatus, showButton };
    }
    async function completeProfileLink(session, current) {
        const params = new URLSearchParams(location.search);
        const ticket = params.get('oidcLinkTicket');
        const error = params.has('oidcLinkError');
        if (!ticket && !error) return;
        params.delete('oidcLinkTicket'); params.delete('oidcLinkError');
        history.replaceState(null, '', location.pathname + (params.size ? '?' + params : '') + location.hash);
        if (error) { linkNotification(profileStrings.linkError, true); return; }
        const response = await linkRequest(session, 'link/complete', { method: 'POST', body: JSON.stringify({ Ticket: ticket }) });
        if (current()) linkNotification(response.ok ? profileStrings.linkSuccess : profileStrings.linkError, !response.ok);
    }
    function mountProfile(content, session, route, key) {
        const { section, headingIcon, headingName, help, button, buttonIcon, label, setStatus, showButton } = createProfileCard(content, key);
        if (isPrimaryLogin()) { section.passwordLoginDisabled = true; setProfilePasswordVisibility(content, true); }
        const current = () => section === profileSection && section.isConnected && activeSession()?.token === session.token;
        async function unlink(state) {
            try {
                if (!state.PartialRevocation && !await confirmUnlink(state, true)) return;
                if (!current()) return;
                button.disabled = true;
                const response = await linkRequest(session, state.PartialRevocation ? 'link/retry-sign-out' : 'link?version=' + state.Version, { method: state.PartialRevocation ? 'POST' : 'DELETE', ...(state.PartialRevocation ? { body: JSON.stringify({ Operation: state.UnlinkOperation }) } : {}) });
                const me = await requestCurrentUser(session.token);
                if (me.status === 401) { if (current()) returnToSignIn(session); return; }
                if (!current()) return;
                if (!response.ok) throw new Error('Unlink failed');
                const result = await response.json();
                let message = profileStrings.adminUnlinked;
                if (result.PartialRevocation) message = profileStrings.adminPartial;
                else if (state.PartialRevocation) message = profileStrings.adminSignedOut;
                linkNotification(message, result.PartialRevocation);
                await loadStatus();
                if (current()) button.focus();
            } catch {
                if (current()) { button.disabled = false; linkNotification(profileStrings.adminError, true); }
            }
        }
        async function initiateLink() {
            button.disabled = true; label.textContent = profileStrings.linkRedirecting;
            try {
                const response = await linkRequest(session, 'link?returnUrl=' + encodeURIComponent(route.pathname + route.search), { method: 'POST' });
                if (!response.ok) throw new Error('Link initiation failed');
                const result = await response.json();
                if (current()) location.assign(result.Url);
            } catch {
                if (current()) { button.disabled = false; label.textContent = profileStrings.linkButton; linkNotification(profileStrings.linkError, true); }
            }
        }
        async function loadStatus() {
            showButton(false); buttonIcon.classList.add('hide'); help.classList.add('hide'); setStatus(profileStrings.linkChecking);
            try {
                Object.assign(profileStrings, await loadStrings());
                if (!current()) return;
                const response = await linkRequest(session, 'link');
                if (!current()) return;
                if (response.status === 404) { profileUnavailableKey = key; section.remove(); restoreProfilePasswords(); return; }
                if (!response.ok) throw new Error('Link status unavailable');
                const state = await response.json();
                if (!current()) return;
                headingName.textContent = providerName(state.Provider, state.ProviderName);
                updateProviderIcon(headingIcon, state.Provider);
                section.passwordLoginDisabled = state.PasswordLoginDisabled;
                setProfilePasswordVisibility(content, state.PasswordLoginDisabled);
                const status = identityStatus(state);
                setStatus(status.text, status);
                const messages = [];
                if (state.PartialRevocation) messages.push(profileStrings.adminPartial);
                if (state.AutomaticReattachmentBlocked) messages.push(profileStrings.adminBlocked);
                if (state.CanLink && state.PasswordLoginMode === 'DisableForLinkedUsersOnly') messages.push(profileStrings.linkPasswordHelp);
                help.textContent = messages.join(' ');
                help.classList.toggle('hide', !help.textContent);
                if (state.PartialRevocation) help.setAttribute('role', 'alert'); else help.removeAttribute('role');
                showButton(state.CanLink || state.CanUnlink || state.PartialRevocation);
                buttonIcon.classList.remove('hide');
                button.disabled = false;
                button.classList.toggle('button-delete', state.CanUnlink && !state.PartialRevocation);
                if (state.PartialRevocation) {
                    buttonIcon.textContent = 'refresh';
                    label.textContent = profileStrings.adminRetrySignOut;
                } else if (state.CanUnlink) {
                    buttonIcon.textContent = 'link_off';
                    label.textContent = profileStrings.adminUnlink;
                } else {
                    buttonIcon.textContent = 'link';
                    label.textContent = profileStrings.linkButton;
                }
                button.onclick = () => {
                    if (state.CanUnlink || state.PartialRevocation) return unlink(state);
                    return initiateLink();
                };
            } catch {
                if (!current()) return;
                setStatus(profileStrings.linkStatusError);
                buttonIcon.classList.add('hide');
                showButton(true); button.disabled = false; label.textContent = profileStrings.linkRetry;
                button.onclick = () => { void loadStatus(); };
            }
        }
        if (!completingLink) completingLink = completeProfileLink(session, current).catch(() => { if (current()) linkNotification(profileStrings.linkError, true); }).finally(() => { completingLink = null; });
        void completingLink.then(loadStatus);
    }
    const updateLoginContainer = button => {
        const login = button.closest('#loginPage');
        const form = login?.querySelector('form.manualLoginForm');
        const stack = login?.querySelector('.readOnlyContent');
        if (!form || !stack) return;
        if (isPrimaryLogin()) {
            form.dataset.oidcLoginContainer = 'true';
            display([form], 'block');
            display([...localControls(form), ...login.querySelectorAll('.visualLoginForm, .btnManual, .btnForgotPassword')], 'none');
            const submit = form.querySelector('button[type="submit"]');
            if (submit && button.nextElementSibling !== submit) submit.before(button);
            else if (!submit && button.parentElement !== form) form.append(button);
        } else {
            form.removeAttribute('data-oidc-login-container');
            display([form, ...localControls(form), ...login.querySelectorAll('.visualLoginForm, .btnManual, .btnForgotPassword, .btnQuick, .btnSelectServer, .loginDisclaimerContainer')]);
            const quick = stack.querySelector('.btnQuick');
            if (button.parentElement !== stack || button.nextElementSibling !== quick) stack.insertBefore(button, quick);
        }
    };
    const addLogin = stack => {
        if (!isLoginPage() || window.oidcEnabled === false || window.oidcHandoff) {
            document.querySelectorAll('form.manualLoginForm[data-oidc-login-container]').forEach(form => form.removeAttribute('data-oidc-login-container'));
            document.querySelectorAll('[data-oidc-login]').forEach(button => button.remove());
            return;
        }
        if (!stack) {
            document.querySelectorAll('.readOnlyContent').forEach(addLogin);
            return;
        }
        const existingButton = stack.closest('#loginPage')?.querySelector('[data-oidc-login]');
        if (existingButton) {
            updateLoginContainer(existingButton);
            return;
        }
        const button = document.createElement('button');
        button.type = 'button'; button.setAttribute('is', 'emby-button'); button.className = 'raised block' + (isPrimaryLogin() ? ' button-submit' : '');
        button.dataset.oidcLogin = 'true';
        const content = document.createElement('span'); content.className = 'oidc-login-content';
        const icon = document.createElement('span'); icon.dataset.oidcProviderIcon = ''; icon.setAttribute('aria-hidden', 'true');
        const label = document.createElement('span'); label.dataset.oidcLoginLabel = 'true';
        content.append(icon, label); button.append(content);
        updateLoginPresentation(button);
        button.onclick = () => {
            button.disabled = true;
            button.setAttribute('aria-label', profileStrings.linkRedirecting);
            label.textContent = profileStrings.linkRedirecting;
            location.assign(endpoint());
        };
        stack.insertBefore(button, stack.querySelector('.btnQuick'));
        updateLoginContainer(button);
    };
    const resetLoginButton = () => {
        document.querySelectorAll('[data-oidc-login]').forEach(button => {
            button.disabled = false;
            updateLoginPresentation(button);
            button.classList.toggle('button-submit', isPrimaryLogin());
            updateLoginContainer(button);
        });
    };
    const showSignedOut = () => {
        if (!isLoginPage() || !isSignedOut()) return false;
        let shown = false;
        for (const login of document.querySelectorAll('#loginPage')) {
            const form = login.querySelector('form.manualLoginForm[data-oidc-login-container]');
            const stack = login.querySelector('.readOnlyContent');
            const heading = form?.querySelector('h1');
            if (!form || !heading || !stack) continue;
            if (heading.textContent !== signedOutLabel()) heading.textContent = signedOutLabel();
            form.style.setProperty('--loginPageText', JSON.stringify(signedOutLabel()));
            addLogin(stack);
            stack.querySelector('[data-oidc-login]')?.classList.add('button-submit');
            display([...login.querySelectorAll('.btnQuick, .btnSelectServer, .loginDisclaimerContainer'), form.querySelector('.chkRememberLogin')?.closest('.checkboxContainer')], 'none');
            login.style.setProperty('visibility', 'visible', 'important');
            shown = true;
        }
        return shown;
    };
    const showError = () => {
        if (!hasOidcError()) {
            shownError = false;
            return;
        }
        if (!nativeLoginReady || !isLoginPage() || document.querySelector('[data-oidc-error]') || shownError) return;
        shownError = true;
        const message = linkNotification(profileStrings.signInError, true, 5000);
        message.dataset.oidcError = 'true';
    };
    let loggingOut;
    const enableLogout = () => addEventListener('click', async event => {
        const menuItem = event.target.closest('[role="menuitem"]');
        const logout = event.target.closest('.btnLogout') || (menuItem?.querySelector('[data-testid="LogoutIcon"]') ? menuItem : null);
        if (!logout || (!window.oidcRpInitiatedLogout && !window.oidcRedirectSignInPageToProvider)) return;
        const session = activeSession();
        if (!session) return;
        event.preventDefault(); event.stopImmediatePropagation();
        if (loggingOut) return;
        loggingOut = true;
        let confirmed = false;
        try {
            const response = await fetch(serverUrl + 'Sessions/Logout', { method: 'POST', signal: AbortSignal.timeout(10000), headers: { Authorization: `MediaBrowser Token="${session.token}"` } });
            confirmed = response.ok || response.status === 401;
        } catch { /* Local sign-out must complete even when the server cannot be reached. */ }
        finally {
            const credentials = savedCredentials();
            for (const server of credentials.Servers || []) if (server.AccessToken === session.token) {
                server.UserId = null; server.AccessToken = null; server.ExchangeToken = null;
            }
            localStorage.setItem('jellyfin_credentials', JSON.stringify(credentials));
            location.assign(confirmed ? oidcUrl + 'logout' : serverUrl + 'web/index.html?oidcSignedOut=1&oidcLogoutError=1#!/login');
        }
    }, true);
    const redirectToProvider = async () => {
        if (window.oidcHandoff || redirectingToProvider || !isLoginPage() || isSignedOut() || !window.oidcRedirectSignInPageToProvider) return;
        if (hasOidcError()) {
            revealLogin();
            return;
        }
        const server = (savedCredentials().Servers || []).find(server => {
            try {
                const address = new URL(server.ManualAddress || server.LocalAddress || '', location.href);
                return address.origin === serverUrl.origin && address.pathname.replace(/\/$/, '') === serverUrl.pathname.replace(/\/$/, '');
            } catch { return false; }
        });
        if (server?.AccessToken && await requestCurrentUser(server.AccessToken).then(response => response.ok).catch(() => false)) return;
        if (redirectingToProvider) return;
        redirectingToProvider = true;
        location.assign(endpoint());
    };
    const configure = async () => {
        Object.assign(profileStrings, await loadStrings());
        const errorMessage = document.querySelector('[data-oidc-error]');
        if (errorMessage) errorMessage.textContent = profileStrings.signInError;
        if (new URLSearchParams(location.search).has('oidcLogoutError')) linkNotification(profileStrings.logoutError, true, 10000);
        const response = await fetch(oidcUrl + 'config', { cache: 'no-store' });
        const config = response.ok ? await response.json() : null;
        window.oidcEnabled = !!config;
        if (!config) {
            window.oidcProvider = 'Other';
            window.oidcProviderName = '';
            window.oidcRedirectSignInPageToProvider = false; window.oidcRpInitiatedLogout = false;
            window.oidcPasswordLoginMode = 'AllowForAllUsers'; revealLogin(); addLogin(); return;
        }
        window.oidcProviderName = config.ProviderName;
        window.oidcProvider = config.Provider;
        window.oidcRpInitiatedLogout = config.RpInitiatedLogout;
        window.oidcPasswordLoginMode = config.PasswordLoginMode;
        window.oidcRedirectSignInPageToProvider = config.RedirectSignInPageToProvider;
        resetLoginButton();
        const redirectsToProvider = config.PasswordLoginMode === 'DisableForAllUsers' && config.RedirectSignInPageToProvider;
        if (redirectsToProvider && isSignedOut()) {
            window.oidcSignedOutText = (await loadStrings().catch(() => ({}))).signedOut;
            if (showSignedOut()) return;
            const observer = new MutationObserver(() => {
                if (showSignedOut()) observer.disconnect();
            });
            observer.observe(document.documentElement, { childList: true, subtree: true });
            return;
        }
        if (redirectsToProvider) void redirectToProvider();
        showError();
    };
    addEventListener('oidcconfigurationchange', () => { profileUnavailableKey = null; profileSection?.remove(); profileSection = null; updateProfile(); void configure(); });
    addEventListener('hashchange', () => {
        if (isSignedOut() && isLoginPage()) {
            location.reload();
            return;
        }
        addLogin();
        void installSession();
        updateProfile();
        showSignedOut();
        showError();
        void redirectToProvider();
    });
    addEventListener('pageshow', () => { redirectingToProvider = false; loggingOut = false; resetLoginButton(); profileSection?.remove(); profileSection = null; void installSession(); updateProfile(); void redirectToProvider(); });
    addEventListener('storage', updateProfile);
    enableLogout();
    new MutationObserver(() => { addLogin(); void installSession(); updateProfile(); showSignedOut(); showError(); void redirectToProvider(); }).observe(document.documentElement, { childList: true, subtree: true });
    addLogin();
    document.querySelectorAll('[data-oidc-login]').forEach(updateLoginContainer);
    void redirectToProvider();
    void configure();

    let adminEditor;
    const adminRoute = () => {
        const route = new URL(location.hash.replace(/^#!?/, ''), location.origin);
        const match = route.pathname.match(/^\/dashboard\/users\/([a-f\d-]+)\/[^/]+$/i);
        return match ? { userId: match[1], selected: route.searchParams.get('oidc') === '1' } : null;
    };
    function clearAdminEditor() {
        if (!adminEditor) return;
        adminEditor.tab?.remove(); adminEditor.panel?.remove();
        adminEditor.page.removeAttribute('data-oidc-admin-selected');
        adminEditor.page.querySelectorAll('.oidc-admin-native-hidden').forEach(element => element.classList.remove('oidc-admin-native-hidden'));
        for (const tab of adminEditor.nativeSelection) {
            const selected = tab.classList.contains('Mui-selected');
            tab.setAttribute('aria-selected', String(selected)); tab.tabIndex = selected ? 0 : -1;
        }
        adminEditor = null;
    }
    function updateAdminEditor() {
        const route = adminRoute();
        const session = activeSession();
        const page = document.getElementById('usersEditPage');
        const tabs = page?.querySelector('[role="tablist"]');
        if (!route || !session || !tabs) { clearAdminEditor(); return; }
        if (!adminEditor || adminEditor.page !== page || adminEditor.tabs !== tabs
            || adminEditor.userId !== route.userId || adminEditor.session.token !== session.token) {
            clearAdminEditor();
            const editor = { page, tabs, userId: route.userId, session, nativeSelection: new Set() };
            adminEditor = editor;
            void requestCurrentUser(session.token).then(response => response.ok ? response.json() : null).then(user => {
                if (adminEditor !== editor || activeSession()?.token !== session.token) return;
                editor.allowed = user?.Policy?.IsAdministrator === true;
                updateAdminEditor();
            }).catch(() => {});
        }
        const editor = adminEditor;
        if (!editor.allowed) return;
        const selectionChanged = editor.selected !== route.selected;
        editor.selected = route.selected;
        let tabBar = tabs;
        while (tabBar.parentElement && !tabBar.parentElement.querySelector(':scope > h1')) tabBar = tabBar.parentElement;
        if (!tabBar.parentElement || !page.contains(tabBar)) return;
        if (!editor.tab?.isConnected) {
            const nativeTab = tabs.querySelector('[role="tab"]');
            if (!nativeTab) return;
            const tab = nativeTab.cloneNode(false);
            tab.id = 'oidc-admin-tab'; tab.textContent = 'OIDC'; tab.type = 'button';
            tab.removeAttribute('aria-labelledby'); tab.setAttribute('aria-controls', 'oidc-admin-panel');
            tab.onclick = event => { event.stopPropagation(); location.hash = '/dashboard/users/' + route.userId + '/profile?oidc=1'; };
            editor.tab = tab; tabs.append(tab);
        }
        page.toggleAttribute('data-oidc-admin-selected', route.selected);
        editor.tab.setAttribute('aria-selected', String(route.selected));
        editor.tab.tabIndex = route.selected ? 0 : -1;
        editor.tab.classList.toggle('Mui-selected', route.selected);
        const nativeTabs = [...tabs.querySelectorAll('[role="tab"]')].filter(tab => tab !== editor.tab);
        if (route.selected) {
            for (const tab of nativeTabs) {
                editor.nativeSelection.add(tab);
                tab.setAttribute('aria-selected', 'false'); tab.tabIndex = -1;
            }
            if (!editor.panel?.isConnected) mountAdminPanel(editor, tabBar.parentElement);
        } else {
            for (const tab of editor.nativeSelection) {
                const selected = tab.classList.contains('Mui-selected');
                tab.setAttribute('aria-selected', String(selected)); tab.tabIndex = selected ? 0 : -1;
            }
            editor.nativeSelection.clear(); editor.panel?.remove(); editor.panel = null;
        }
        let afterTabs = false;
        for (const child of tabBar.parentElement.children) {
            if (child === tabBar) afterTabs = true;
            else if (afterTabs && child !== editor.panel) child.classList.toggle('oidc-admin-native-hidden', route.selected);
        }
        if (selectionChanged) revealAdminTab();
    }
    function revealAdminTab() {
        if (!adminEditor?.selected || !adminEditor.tab?.isConnected) return;
        const { tabs, tab } = adminEditor;
        tabs.scrollLeft = Math.max(0, tab.offsetLeft + tab.offsetWidth - tabs.clientWidth);
    }
    function mountAdminPanel(editor, content) {
        const { userId, session } = editor;
        const panel = document.createElement('section'); editor.panel = panel;
        panel.id = 'oidc-admin-panel'; panel.className = 'readOnlyContent'; panel.dataset.oidcAdmin = userId;
        panel.setAttribute('role', 'tabpanel'); panel.setAttribute('aria-labelledby', 'oidc-admin-tab');
        content.append(panel);
        const current = () => adminEditor === editor && editor.panel === panel && panel.isConnected
            && activeSession()?.token === session.token && adminRoute()?.selected;
        const path = 'admin/users/' + encodeURIComponent(userId);
        let state, error = '', busy = false, editing = false, matchBy = 'Subject', value = '';
        const node = (tag, text = '', className = '') => {
            const element = document.createElement(tag); element.textContent = text; element.className = className; return element;
        };
        const message = (text, alert = false) => {
            const element = node('p', text, 'fieldDescription'); if (alert) element.setAttribute('role', 'alert'); return element;
        };
        const button = (key, onClick, icon = '', className = 'raised') => {
            const element = node('button', '', 'emby-button ' + className); element.type = 'button'; element.setAttribute('is', 'emby-button');
            element.style.alignSelf = 'flex-start'; element.disabled = busy;
            if (icon) { const glyph = node('span', icon, 'material-icons'); glyph.setAttribute('aria-hidden', 'true'); element.append(glyph); }
            element.append(node('span', profileStrings[key])); element.onclick = onClick; return element;
        };
        const setBusy = value => {
            busy = value; panel.setAttribute('aria-busy', String(busy));
            if (busy) panel.querySelectorAll('button, input, select').forEach(element => { element.disabled = true; });
        };
        async function load() {
            setBusy(true); error = ''; state = null; editing = false; render();
            try {
                Object.assign(profileStrings, await loadStrings());
                if (!current()) return;
                const response = await linkRequest(session, path);
                if (!response.ok) throw new Error('Link status unavailable');
                const result = await response.json();
                if (current()) state = result;
            } catch { if (current()) error = profileStrings.linkStatusError; }
            finally { if (current()) { setBusy(false); render(); } }
        }
        async function mutate(suffix, method, data) {
            if (!current() || busy) return;
            setBusy(true); error = '';
            try {
                const response = await linkRequest(session, path + suffix, { method, ...(data ? { body: JSON.stringify(data) } : {}) });
                if (suffix.startsWith('/link?') || suffix === '/retry-sign-out') {
                    const me = await requestCurrentUser(session.token);
                    if (me.status === 401) { if (current()) returnToSignIn(session); return; }
                }
                if (!current()) return;
                if (!response.ok) { error = profileStrings[response.status === 409 ? 'adminConflict' : 'adminError']; return; }
                const result = await response.json();
                if (!current()) return;
                state = result; editing = false;
                let outcome = 'adminSignedOut';
                if (suffix === '/link-request') outcome = 'adminApproved';
                else if (suffix.startsWith('/link-request?')) outcome = 'adminCancelled';
                else if (suffix.startsWith('/link?')) outcome = 'adminUnlinked';
                linkNotification(profileStrings[result.PartialRevocation && outcome === 'adminSignedOut' ? 'adminPartial' : outcome]);
            } catch { if (current()) error = profileStrings.adminError; }
            finally { if (current()) { setBusy(false); render(); } }
        }
        function approve() {
            matchBy = state.Request ? approvalCriterion(state.Request) : 'Subject';
            value = state.Request?.Subject || state.Request?.Email || state.Request?.Username || '';
            editing = true; render(); panel.querySelector('#oidc-admin-value').focus();
        }
        async function unlink() {
            try { if (!await confirmUnlink(state, sameUser(userId, session.userId))) return; }
            catch { return; }
            if (!current()) return;
            await mutate('/link?version=' + state.Version, 'DELETE');
            if (current()) (panel.querySelector(':scope > button:not(:disabled)') || editor.tab).focus();
        }
        function approvalForm() {
            const form = node('form');
            const selectContainer = node('div', '', 'selectContainer');
            const select = document.createElement('select', 'emby-select');
            select.id = 'oidc-admin-match-by'; select.setAttribute('label', profileStrings.adminMatchBy);
            for (const kind of ['Subject', 'Email', 'Username']) {
                const option = node('option', profileStrings['admin' + kind]); option.value = kind; select.append(option);
            }
            select.value = matchBy; select.disabled = busy; selectContainer.append(select);
            const inputContainer = node('div', '', 'inputContainer');
            const label = node('label', profileStrings.adminValue, 'inputLabel'); label.htmlFor = 'oidc-admin-value';
            const input = node('input', '', 'emby-input'); input.id = 'oidc-admin-value'; input.required = true; input.disabled = busy;
            input.setAttribute('aria-describedby', 'oidc-admin-match-help'); inputContainer.append(label, input);
            const help = message(''); help.id = 'oidc-admin-match-help';
            const cancel = button('adminCancel', () => { editing = false; render(); });
            const submit = button('adminSave', null, '', 'raised button-submit'); submit.type = 'submit';
            const validate = () => { value = input.value; submit.disabled = busy || !value.trim() || !input.validity.valid || value.length > input.maxLength; };
            const update = () => {
                matchBy = select.value; input.type = matchBy === 'Email' ? 'email' : 'text'; input.maxLength = matchBy === 'Email' ? 320 : 1024;
                help.textContent = profileStrings['admin' + matchBy + 'Help']; validate();
            };
            input.type = matchBy === 'Email' ? 'email' : 'text'; input.value = value;
            input.oninput = validate; select.onchange = update; update();
            form.append(selectContainer, inputContainer, help, message(profileStrings.adminApprovalHelp), cancel, submit);
            form.onsubmit = event => { event.preventDefault(); validate(); if (!submit.disabled) void mutate('/link-request', 'PUT', { Version: state.Version, [matchBy]: value }); };
            return form;
        }
        function render() {
            const children = [];
            if (error) children.push(message(error, true), button('linkRetry', load));
            if (!state && busy) { const checking = message(profileStrings.linkChecking); checking.setAttribute('role', 'status'); children.push(checking); }
            if (state) {
                const status = identityStatus(state);
                const line = node('div', '', 'oidc-admin-status-line');
                const provider = node('span', '', 'oidc-admin-provider');
                const providerIcon = node('span'); providerIcon.dataset.oidcProviderIcon = ''; providerIcon.setAttribute('aria-hidden', 'true'); updateProviderIcon(providerIcon, state.Provider);
                const name = node('span', providerName(state.Provider, state.ProviderName)); name.dataset.oidcAdminProviderName = 'true'; provider.append(providerIcon, name);
                const identity = node('p', '', 'listItemBodyText secondaryText'); identity.setAttribute('role', 'status');
                Object.assign(identity.style, { margin: '0', minWidth: '0', overflowWrap: 'anywhere', whiteSpace: 'normal', color: status.color || '' });
                const icon = node('span', status.icon, 'material-icons'); icon.setAttribute('aria-hidden', 'true'); Object.assign(icon.style, statusIconStyle);
                const label = node('span', status.text); label.dataset.oidcStatusText = 'true'; identity.append(icon, label); line.append(provider, identity); children.push(line);
                if (!state.OidcAvailable) children.push(message(profileStrings.adminDisabled, true));
                if (state.AutomaticReattachmentBlocked) children.push(message(profileStrings.adminBlocked));
                if (state.PartialRevocation) children.push(message(profileStrings.adminPartial, true), button('adminRetrySignOut', () => mutate('/retry-sign-out', 'POST', { Operation: state.UnlinkOperation }), 'refresh'));
                if (state.Request && !editing) children.push(message(profileStrings['admin' + approvalCriterion(state.Request)] + ': ' + (state.Request.Subject || state.Request.Username || state.Request.Email)), message(profileStrings.adminApprovalHelp));
                if (editing) children.push(approvalForm());
                else if (state.Linked) children.push(button('adminUnlink', unlink, 'link_off', 'raised button-delete'));
                else {
                    const approval = button(state.Request ? 'adminEdit' : 'adminApprove', approve, state.Request ? 'edit' : 'link');
                    approval.disabled = busy || !state.CanCreateRequest; children.push(approval);
                    if (state.Request) children.push(button('adminCancelRequest', () => mutate('/link-request?version=' + state.Version, 'DELETE')));
                }
            }
            panel.replaceChildren(...children);
        }
        void load();
    }
    const adminStyle = document.createElement('style');
    adminStyle.textContent = '#oidc-admin-panel { display: flex; flex-direction: column; gap: 1rem; min-width: 0; overflow-wrap: anywhere; } #oidc-admin-panel p { margin-block: 0; } #oidc-admin-panel form { width: 100%; } #oidc-admin-panel form .inputContainer, #oidc-admin-panel form .selectContainer { margin-bottom: 1rem; } #oidc-admin-panel form button + button { margin-inline-start: 0.5rem; } #usersEditPage .oidc-admin-native-hidden { display: none !important; } #usersEditPage [role="tablist"] { overflow-x: auto; scrollbar-width: thin; } #usersEditPage [role="tab"] { flex-shrink: 0; } #oidc-admin-tab[aria-selected="true"] { border-bottom: 2px solid var(--jf-palette-primary-main); } #usersEditPage[data-oidc-admin-selected] .MuiTabs-indicator { visibility: hidden; } #usersEditPage[data-oidc-admin-selected] [role="tab"]:not(#oidc-admin-tab) { color: var(--jf-palette-text-secondary); } #oidc-admin-tab:focus-visible { outline: 2px solid var(--jf-palette-primary-main); outline-offset: -2px; }';
    document.head.append(adminStyle);
    addEventListener('hashchange', updateAdminEditor);
    addEventListener('pageshow', updateAdminEditor);
    addEventListener('resize', revealAdminTab);
    addEventListener('oidcconfigurationchange', () => { clearAdminEditor(); updateAdminEditor(); });
    new MutationObserver(updateAdminEditor).observe(document.documentElement, { childList: true, subtree: true });
    updateAdminEditor();
})();
