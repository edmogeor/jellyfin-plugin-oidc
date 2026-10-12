(() => {
    const oidcUrl = new URL('.', document.currentScript.src);
    const serverUrl = new URL('../', oidcUrl);
    const endpoint = () => oidcUrl + 'start?returnUrl=' + encodeURIComponent(location.hash.startsWith('#!') ? location.hash.slice(2) : '/');
    const isLoginPage = () => location.hash.includes('login');
    const hasOidcError = () => location.search.includes('oidcError=1') || location.hash.includes('oidcError=1');
    const isSignedOut = () => location.search.includes('oidcSignedOut=1');
    const loginLabel = () => window.oidcButtonText || 'Sign In with SSO';
    const signedOutLabel = () => window.oidcSignedOutText || 'Signed Out';
    const revealLogin = () => document.querySelector('#oidc-login-redirect-style')?.remove();
    const isPrimaryLogin = () => window.oidcPasswordLoginMode === 'DisableForAllUsers';
    let shownError;
    let redirectingToProvider;
    const display = (elements, value) => elements.filter(Boolean).forEach(element => value ? element.style.setProperty('display', value, 'important') : element.style.removeProperty('display'));
    const localControls = form => [
        ...form.querySelectorAll(':scope > .inputContainer, :scope > .checkboxContainer, :scope > button[type="submit"]'),
        form.querySelector('.btnCancel')?.parentElement,
    ];
    const loadStrings = async () => {
        const locale = document.documentElement.lang.toLowerCase();
        for (const value of new Set([locale, locale.split('-')[0], 'en-us'])) {
            const response = await fetch(oidcUrl + 'strings/' + encodeURIComponent(value));
            if (response.ok) return response.json();
        }
        return {};
    };
    const profileStrings = {
        linkHeading: 'SSO', linkChecking: 'Checking link status...', linkNotLinked: 'Not linked',
        linkLinked: 'Linked', linkLinkedAs: 'Linked as {0}', linkButton: 'Link account',
        linkRedirecting: 'Redirecting...', linkStatusError: 'Unable to check link status.', linkRetry: 'Retry',
        linkError: 'We couldn\'t link your Identity Provider. Please try again.', linkSuccess: 'Identity Provider linked.',
        linkPasswordHelp: 'Linking will disable password sign-in for this Jellyfin User.',
        linkConflict: 'This identity cannot be linked. Ask an administrator to check existing Identity Links.',
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
    const linkNotification = (text, error = false) => {
        const container = document.querySelector('.toastContainer') || document.body.appendChild(document.createElement('div'));
        container.classList.add('toastContainer');
        const message = document.createElement('div');
        message.className = 'toast'; message.textContent = text; message.setAttribute('role', error ? 'alert' : 'status');
        container.append(message);
        setTimeout(() => message.classList.add('toastVisible'), 300);
        setTimeout(() => message.remove(), 5000);
    };
    let profileSection;
    let profileUnavailableKey;
    let completingLink;
    const restoreProfilePasswords = () => document.querySelectorAll('[data-oidc-password-hidden]').forEach(form => {
        form.removeAttribute('data-oidc-password-hidden'); display([form]);
    });
    const updateProfile = () => {
        const session = activeSession();
        const route = profileRoute();
        const requestedUser = route?.searchParams.get('userId');
        const content = document.querySelector('#userProfilePage .padded-left.padded-right.padded-bottom-page');
        if (!session || !route || !content || (requestedUser && requestedUser.replaceAll('-', '').toLowerCase() !== session.userId.replaceAll('-', '').toLowerCase())) {
            profileSection?.remove(); profileSection = null;
            restoreProfilePasswords();
            return;
        }
        const setPasswordVisibility = disabled => {
            if (disabled === undefined) return;
            const form = content.querySelector('.updatePasswordForm');
            if (!form) return;
            if (disabled) { form.dataset.oidcPasswordHidden = 'true'; display([form], 'none'); }
            else if (form.hasAttribute('data-oidc-password-hidden')) { form.removeAttribute('data-oidc-password-hidden'); display([form]); }
        };
        const key = session.userId + ':' + session.token;
        if (profileUnavailableKey === key) return;
        if (profileSection?.isConnected && profileSection.sessionKey === key) {
            setPasswordVisibility(profileSection.passwordLoginDisabled);
            return;
        }
        profileSection?.remove();
        restoreProfilePasswords();
        const section = document.createElement('section');
        profileSection = section; section.sessionKey = key;
        section.dataset.oidcProfile = 'true'; section.className = 'readOnlyContent detailSection';
        section.style.margin = '0 auto 2em';
        const card = document.createElement('div'); card.className = 'paperList';
        const body = document.createElement('div'); body.className = 'listItemBody';
        Object.assign(body.style, { display: 'flex', flexDirection: 'row', alignItems: 'center', flexWrap: 'wrap', gap: '1rem', padding: '1em', boxSizing: 'border-box', width: '100%' });
        const details = document.createElement('div'); Object.assign(details.style, { flex: '1 1 14rem', minWidth: '0' });
        const heading = document.createElement('div'); heading.className = 'listItemBodyText'; heading.textContent = profileStrings.linkHeading;
        heading.setAttribute('role', 'heading'); heading.setAttribute('aria-level', '2');
        Object.assign(heading.style, { fontWeight: '600', color: 'var(--jf-palette-text-primary, inherit)' });
        const status = document.createElement('p'); status.className = 'listItemBodyText secondaryText'; status.setAttribute('role', 'status');
        status.style.overflowWrap = 'anywhere';
        const icon = document.createElement('span'); icon.className = 'material-icons hide'; icon.setAttribute('aria-hidden', 'true');
        Object.assign(icon.style, { fontSize: '1.1em', verticalAlign: 'middle', marginInlineEnd: '0.3em' });
        const statusText = document.createElement('span'); statusText.dataset.oidcStatusText = 'true'; status.append(icon, statusText);
        const setStatus = (text, linked = null) => {
            statusText.textContent = text;
            icon.classList.toggle('hide', linked === null);
            icon.textContent = linked ? 'check' : 'close';
            if (linked === null) status.style.removeProperty('color');
            else status.style.color = linked ? 'var(--jf-palette-success-main)' : 'var(--jf-palette-error-main)';
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
        if (isPrimaryLogin()) { section.passwordLoginDisabled = true; setPasswordVisibility(true); }
        const current = () => section === profileSection && section.isConnected && activeSession()?.token === session.token;
        const loadStatus = async () => {
            showButton(false); buttonIcon.classList.add('hide'); help.classList.add('hide'); setStatus(profileStrings.linkChecking);
            try {
                Object.assign(profileStrings, await loadStrings());
                if (!current()) return;
                heading.textContent = profileStrings.linkHeading;
                const response = await linkRequest(session, 'link');
                if (!current()) return;
                if (response.status === 404) { profileUnavailableKey = key; section.remove(); restoreProfilePasswords(); return; }
                if (!response.ok) throw new Error('Link status unavailable');
                const state = await response.json();
                if (!current()) return;
                section.passwordLoginDisabled = state.PasswordLoginDisabled;
                setPasswordVisibility(state.PasswordLoginDisabled);
                const linkedIdentity = state.Email || state.Username;
                setStatus(state.Linked ? (linkedIdentity ? profileStrings.linkLinkedAs.replace('{0}', linkedIdentity) : profileStrings.linkLinked) : profileStrings.linkNotLinked, state.Linked);
                help.textContent = state.CanLink ? (state.PasswordLoginMode === 'DisableForLinkedUsersOnly' ? profileStrings.linkPasswordHelp : '') : profileStrings.linkConflict;
                help.classList.toggle('hide', state.Linked || !help.textContent);
                showButton(state.CanLink && !state.Linked);
                buttonIcon.classList.remove('hide');
                button.disabled = false; label.textContent = profileStrings.linkButton;
                button.onclick = async () => {
                    button.disabled = true; label.textContent = profileStrings.linkRedirecting;
                    try {
                        const response = await linkRequest(session, 'link?returnUrl=' + encodeURIComponent(route.pathname + route.search), { method: 'POST' });
                        if (!response.ok) throw new Error('Link initiation failed');
                        const result = await response.json();
                        if (current()) location.assign(result.Url);
                    } catch {
                        if (current()) { button.disabled = false; label.textContent = profileStrings.linkButton; linkNotification(profileStrings.linkError, true); }
                    }
                };
            } catch {
                if (!current()) return;
                setStatus(profileStrings.linkStatusError);
                buttonIcon.classList.add('hide');
                showButton(true); button.disabled = false; label.textContent = profileStrings.linkRetry;
                button.onclick = () => { void loadStatus(); };
            }
        };
        const complete = async () => {
            const params = new URLSearchParams(location.search);
            const ticket = params.get('oidcLinkTicket');
            const error = params.has('oidcLinkError');
            if (!ticket && !error) return;
            params.delete('oidcLinkTicket'); params.delete('oidcLinkError');
            history.replaceState(null, '', location.pathname + (params.size ? '?' + params : '') + location.hash);
            if (error) { linkNotification(profileStrings.linkError, true); return; }
            const response = await linkRequest(session, 'link/complete', { method: 'POST', body: JSON.stringify({ Ticket: ticket }) });
            if (current()) linkNotification(response.ok ? profileStrings.linkSuccess : profileStrings.linkError, !response.ok);
        };
        if (!completingLink) completingLink = complete().catch(() => { if (current()) linkNotification(profileStrings.linkError, true); }).finally(() => { completingLink = null; });
        void completingLink.then(loadStatus);
    };
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
        if (!isLoginPage()) {
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
        button.dataset.oidcLogin = 'true'; button.setAttribute('aria-label', loginLabel());
        const label = document.createElement('span'); label.textContent = loginLabel();
        button.append(label);
        button.onclick = () => {
            button.disabled = true;
            button.setAttribute('aria-label', 'Redirecting...');
            label.textContent = 'Redirecting...';
            location.assign(endpoint());
        };
        stack.insertBefore(button, stack.querySelector('.btnQuick'));
        updateLoginContainer(button);
    };
    const resetLoginButton = () => {
        document.querySelectorAll('[data-oidc-login]').forEach(button => {
            button.disabled = false;
            button.setAttribute('aria-label', loginLabel());
            button.querySelector('span').textContent = loginLabel();
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
            display([...login.querySelectorAll('.btnQuick, .btnSelectServer, .loginDisclaimerContainer')], 'none');
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
        if (!isLoginPage() || document.querySelector('[data-oidc-error]') || shownError) return;
        shownError = true;
        const container = document.querySelector('.toastContainer') || document.body.appendChild(document.createElement('div'));
        container.classList.add('toastContainer');
        const message = document.createElement('div');
        message.dataset.oidcError = 'true'; message.setAttribute('role', 'alert');
        message.className = 'toast'; message.textContent = 'We couldn\'t sign you in. Please try again.';
        container.append(message);
        setTimeout(() => message.classList.add('toastVisible'), 300);
        setTimeout(() => message.remove(), 3600);
    };
    const enableLogout = () => addEventListener('click', async event => {
        const menuItem = event.target.closest('[role="menuitem"]');
        const logout = event.target.closest('.btnLogout') || (menuItem?.querySelector('[data-testid="LogoutIcon"]') ? menuItem : null);
        if (!logout || (!window.oidcRpInitiatedLogout && !window.oidcRedirectSignInPageToProvider)) return;
        event.preventDefault(); event.stopImmediatePropagation();
        const credentials = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
        const server = credentials.Servers?.[0];
        if (server?.AccessToken) await fetch(serverUrl + 'Sessions/Logout', { method: 'POST', headers: { Authorization: `MediaBrowser Token="${server.AccessToken}"` } });
        for (const savedServer of credentials.Servers || []) {
            savedServer.UserId = null; savedServer.AccessToken = null; savedServer.ExchangeToken = null;
        }
        localStorage.setItem('jellyfin_credentials', JSON.stringify(credentials));
        location.assign(oidcUrl + 'logout');
    }, true);
    const redirectToProvider = async () => {
        if (redirectingToProvider || !isLoginPage() || isSignedOut() || !window.oidcRedirectSignInPageToProvider) return;
        if (hasOidcError()) {
            revealLogin();
            return;
        }
        const server = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}').Servers?.[0];
        if (server?.AccessToken && await fetch(serverUrl + 'Users/Me', { headers: { Authorization: `MediaBrowser Token="${server.AccessToken}"` } }).then(response => response.ok).catch(() => false)) return;
        if (redirectingToProvider) return;
        redirectingToProvider = true;
        location.assign(endpoint());
    };
    const configure = async () => {
        const response = await fetch(oidcUrl + 'config', { cache: 'no-store' });
        const config = response.ok ? await response.json() : null;
        if (!config) return;
        window.oidcButtonText = config.LoginButtonText;
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
        updateProfile();
        showSignedOut();
        showError();
        void redirectToProvider();
    });
    addEventListener('pageshow', () => { resetLoginButton(); profileSection?.remove(); profileSection = null; updateProfile(); void redirectToProvider(); });
    addEventListener('storage', updateProfile);
    enableLogout();
    new MutationObserver(() => { addLogin(); updateProfile(); showSignedOut(); showError(); void redirectToProvider(); }).observe(document.documentElement, { childList: true, subtree: true });
    addLogin();
    document.querySelectorAll('[data-oidc-login]').forEach(updateLoginContainer);
    void redirectToProvider();
    void configure();
})();
