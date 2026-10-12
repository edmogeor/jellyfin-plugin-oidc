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
    const profileStrings = /* @english-strings */ {};
    const uiStyle = document.createElement('style');
    uiStyle.textContent = '.formDialogHeader.justify-content-center > .formDialogHeaderTitle { margin-inline: 0; } #oidc-admin-panel .emby-button .material-icons { font-size: 1.2em; margin-inline-end: 0.4em; }';
    document.head.append(uiStyle);
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
        const credentials = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
        for (const server of credentials.Servers || []) if (server.AccessToken === session.token) { server.AccessToken = null; server.UserId = null; server.ExchangeToken = null; }
        localStorage.setItem('jellyfin_credentials', JSON.stringify(credentials));
        location.assign(serverUrl + 'web/index.html?oidcSignedOut=1#!/login');
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
        const content = document.querySelector('#userProfilePage .padded-left.padded-right.padded-bottom-page');
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
        const heading = document.createElement('div'); heading.className = 'listItemBodyText'; heading.textContent = profileStrings.linkHeading;
        heading.setAttribute('role', 'heading'); heading.setAttribute('aria-level', '2');
        Object.assign(heading.style, { fontWeight: '600', color: 'var(--jf-palette-text-primary, inherit)' });
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
        return { section, heading, help, button, buttonIcon, label, setStatus, showButton };
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
        const { section, heading, help, button, buttonIcon, label, setStatus, showButton } = createProfileCard(content, key);
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
                heading.textContent = profileStrings.linkHeading;
                const response = await linkRequest(session, 'link');
                if (!current()) return;
                if (response.status === 404) { profileUnavailableKey = key; section.remove(); restoreProfilePasswords(); return; }
                if (!response.ok) throw new Error('Link status unavailable');
                const state = await response.json();
                if (!current()) return;
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
        if (!isLoginPage() || window.oidcEnabled === false) {
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
        const message = linkNotification('We couldn\'t sign you in. Please try again.', true, 3600);
        message.dataset.oidcError = 'true';
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
        if (server?.AccessToken && await requestCurrentUser(server.AccessToken).then(response => response.ok).catch(() => false)) return;
        if (redirectingToProvider) return;
        redirectingToProvider = true;
        location.assign(endpoint());
    };
    const configure = async () => {
        const response = await fetch(oidcUrl + 'config', { cache: 'no-store' });
        const config = response.ok ? await response.json() : null;
        window.oidcEnabled = !!config;
        if (!config) {
            window.oidcRedirectSignInPageToProvider = false; window.oidcRpInitiatedLogout = false;
            window.oidcPasswordLoginMode = 'AllowForAllUsers'; revealLogin(); addLogin(); return;
        }
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

    // Jellyfin 12 has no dashboard tab-extension hook. Wrap its existing editor at chunk registration,
    // keeping the native React tree, theme, router, Tabs/Tab, inputs, buttons, dialog, and Toast.
    // These module IDs are verified against the pinned Jellyfin 12 test image.
    const installAdminEditor = (module, exports, require, originalFactory) => {
        originalFactory(module, exports, require);
        const Original = module.exports.Component;
        const React = require(63696);
        const h = React.createElement;
        const Tabs = require(26445).A;
        const Tab = require(4994).A;
        const Stack = require(70140).A;
        const Alert = require(47891).A;
        const Input = require(42301).A;
        const Button = require(50723).A;
        const Loading = require(84849).A;
        const Toast = require(45228).A;
        const globalize = require(5898).Ay;
        const adminStrings = profileStrings;
        const text = key => adminStrings[key];
        const cancelText = () => globalize.translate('ButtonCancel');
        const Panel = ({ userId, session }) => {
            const [state, setState] = React.useState(null);
            const [error, setError] = React.useState('');
            const [busy, setBusy] = React.useState(false);
            const [editing, setEditing] = React.useState(false);
            const [matchBy, setMatchBy] = React.useState('Subject');
            const [value, setValue] = React.useState('');
            const [valueValid, setValueValid] = React.useState(false);
            const [toast, setToast] = React.useState('');
            const selectContainer = React.useRef(null);
            const alive = React.useRef(true);
            React.useEffect(() => {
                const input = document.getElementById('oidc-admin-value');
                setValueValid(!!value.trim() && !!input?.validity.valid && input.value.length <= input.maxLength);
            }, [editing, matchBy, value]);
            React.useEffect(() => {
                const container = selectContainer.current;
                if (!container) return;
                let select = container.querySelector('select');
                if (!select) {
                    // Jellyfin's customized built-in uses the legacy registration API, as its native SelectElement does.
                    select = document.createElement('select', 'emby-select');
                    select.id = 'oidc-admin-match-by'; select.setAttribute('label', text('adminMatchBy'));
                    for (const kind of ['Subject', 'Email', 'Username']) {
                        const option = document.createElement('option'); option.value = kind; option.textContent = text('admin' + kind); select.append(option);
                    }
                    container.append(select);
                }
                select.value = matchBy; select.disabled = busy;
                const change = () => setMatchBy(select.value);
                select.addEventListener('change', change);
                return () => select.removeEventListener('change', change);
            }, [editing, matchBy, busy]);
            const current = () => alive.current && activeSession()?.token === session.token;
            const path = 'admin/users/' + encodeURIComponent(userId);
            const load = async () => {
                setBusy(true); setError(''); setState(null); setEditing(false);
                try {
                    Object.assign(adminStrings, await loadStrings());
                    const response = await linkRequest(session, path);
                    if (!response.ok) throw new Error();
                    const value = await response.json();
                    if (current()) setState(value);
                } catch { if (current()) setError(text('linkStatusError')); }
                finally { if (current()) setBusy(false); }
            };
            React.useEffect(() => { alive.current = true; void load(); return () => { alive.current = false; }; }, []);
            const mutate = async (suffix, method, data) => {
                if (!current() || busy) return;
                setBusy(true); setError('');
                try {
                    const response = await linkRequest(session, path + suffix, { method, ...(data ? { body: JSON.stringify(data) } : {}) });
                    if (suffix.startsWith('/link?') || suffix === '/retry-sign-out') {
                        const me = await requestCurrentUser(session.token);
                        if (me.status === 401) {
                            returnToSignIn(session); return;
                        }
                    }
                    if (!response.ok) { if (current()) setError(text(response.status === 409 ? 'adminConflict' : 'adminError')); return; }
                    const value = await response.json();
                    if (current()) {
                        setState(value); setEditing(false);
                        let outcome = 'adminSignedOut';
                        if (suffix === '/link-request') outcome = 'adminApproved';
                        else if (suffix.startsWith('/link-request?')) outcome = 'adminCancelled';
                        else if (suffix.startsWith('/link?')) outcome = 'adminUnlinked';
                        setToast(text(value.PartialRevocation && outcome === 'adminSignedOut' ? 'adminPartial' : outcome));
                    }
                } catch { if (current()) setError(text('adminError')); }
                finally { if (current()) setBusy(false); }
            };
            const button = (key, onClick, props = {}) => h(Button, { key, type: 'button', className: 'raised', title: text(key), iconPos: 'LEFT', style: { alignSelf: 'flex-start' }, disabled: busy, onClick, ...props });
            const approval = () => {
                if (state.Request) setMatchBy(approvalCriterion(state.Request));
                else setMatchBy('Subject');
                setValue(state.Request?.Subject || state.Request?.Email || state.Request?.Username || '');
                setEditing(true);
            };
            const unlink = async () => {
                try { if (!await confirmUnlink(state, sameUser(userId, session.userId))) return; }
                catch { return; }
                if (current()) {
                    await mutate('/link?version=' + state.Version, 'DELETE');
                    requestAnimationFrame(() => {
                        if (current()) (document.querySelector('#oidc-admin-panel > button:not(:disabled)') || document.getElementById('oidc-admin-tab'))?.focus();
                    });
                }
            };
            const children = [];
            if (error) children.push(h(Alert, { key: 'error', severity: 'error' }, error), button('linkRetry', load));
            if (!state && busy) children.push(h(Loading, { key: 'loading' }));
            if (state) {
                const status = identityStatus(state);
                children.push(h('p', { key: 'status', className: 'listItemBodyText secondaryText', role: 'status', style: { margin: 0, color: status.color } },
                    h('span', { className: 'material-icons', 'aria-hidden': 'true', style: statusIconStyle }, status.icon),
                    h('span', { 'data-oidc-status-text': 'true' }, status.text)));
                if (!state.OidcAvailable) children.push(h(Alert, { key: 'disabled', severity: 'warning' }, text('adminDisabled')));
                if (state.AutomaticReattachmentBlocked) children.push(h('p', { key: 'block', className: 'fieldDescription' }, text('adminBlocked')));
                if (state.PartialRevocation) children.push(h(Alert, { key: 'partial', severity: 'warning' }, text('adminPartial')), button('adminRetrySignOut', () => mutate('/retry-sign-out', 'POST', { Operation: state.UnlinkOperation }), { icon: 'refresh' }));
                if (state.Request && !editing) children.push(h('p', { key: 'criteria' }, text('admin' + approvalCriterion(state.Request)) + ': ' + (state.Request.Subject || state.Request.Username || state.Request.Email)), h('p', { key: 'approval-help', className: 'fieldDescription' }, text('adminApprovalHelp')));
                if (editing) children.push(h('form', { key: 'form', onSubmit: event => { event.preventDefault(); if (valueValid) void mutate('/link-request', 'PUT', { Version: state.Version, [matchBy]: value }); } },
                    h('div', { className: 'selectContainer', ref: selectContainer }),
                    h('div', { className: 'inputContainer' }, h(Input, { id: 'oidc-admin-value', label: text('adminValue'), type: matchBy === 'Email' ? 'email' : 'text', value, required: true, maxLength: matchBy === 'Email' ? 320 : 1024, disabled: busy, 'aria-describedby': 'oidc-admin-match-help', onChange: event => setValue(event.target.value) })),
                    h('p', { id: 'oidc-admin-match-help', className: 'fieldDescription' }, text('admin' + matchBy + 'Help')),
                    h('p', { className: 'fieldDescription' }, text('adminApprovalHelp')),
                    h(Button, { type: 'button', className: 'raised', title: cancelText(), disabled: busy, onClick: () => setEditing(false) }),
                    h(Button, { type: 'submit', className: 'raised button-submit', title: text('adminSave'), disabled: busy || !valueValid })));
                else if (state.Linked) children.push(button('adminUnlink', () => { void unlink(); }, { className: 'raised button-delete', icon: 'link_off' }));
                else {
                    children.push(button(state.Request ? 'adminEdit' : 'adminApprove', approval, { icon: state.Request ? 'edit' : 'link', disabled: busy || !state.CanCreateRequest }));
                    if (state.Request) children.push(button('adminCancelRequest', () => mutate('/link-request?version=' + state.Version, 'DELETE')));
                }
            }
            children.push(h(Toast, { key: 'toast', open: !!toast, message: toast, autoHideDuration: 5000, onClose: () => setToast('') }));
            return h(Stack, { className: 'readOnlyContent', spacing: 2, sx: { overflowWrap: 'anywhere', minWidth: 0 }, role: 'tabpanel', id: 'oidc-admin-panel', 'aria-labelledby': 'oidc-admin-tab', 'aria-busy': busy, 'data-oidc-admin': userId }, ...children);
        };
        const Editor = () => {
            const session = activeSession();
            const [admin, setAdmin] = React.useState(false);
            const [, refresh] = React.useState(0);
            React.useEffect(() => { const update = () => refresh(value => value + 1); addEventListener('hashchange', update); return () => removeEventListener('hashchange', update); }, []);
            React.useEffect(() => {
                let active = true; setAdmin(false);
                if (session) void requestCurrentUser(session.token).then(response => response.ok ? response.json() : null).then(user => { if (active) setAdmin(user?.Policy?.IsAdministrator === true); }).catch(() => {});
                return () => { active = false; };
            }, [session?.token]);
            const tree = Original();
            const route = new URL(location.hash.replace(/^#!?/, ''), location.origin);
            const match = route.pathname.match(/^\/dashboard\/users\/([a-f\d-]+)\/[^/]+$/i);
            if (!admin || !session || !match) return tree;
            const userId = match[1];
            const selected = route.searchParams.get('oidc') === '1';
            const extend = element => {
                if (!React.isValidElement(element)) return element;
                const children = React.Children.toArray(element.props.children);
                const tabsIndex = children.findIndex(child => child.type === Tabs);
                if (tabsIndex < 0) return React.cloneElement(element, {}, ...children.map(extend));
                const tabs = children[tabsIndex];
                const choose = (event, value) => {
                    if (value === 'oidc') location.hash = '/dashboard/users/' + userId + '/profile?oidc=1';
                    else tabs.props.onChange(event, value);
                };
                const nativeTabs = React.Children.toArray(tabs.props.children);
                const extendedTabs = React.cloneElement(tabs, { value: selected ? 'oidc' : tabs.props.value, onChange: choose, variant: 'scrollable', scrollButtons: 'auto', allowScrollButtonsMobile: true },
                    ...nativeTabs, h(Tab, { key: 'oidc', label: 'OIDC', value: 'oidc', id: 'oidc-admin-tab', 'aria-controls': 'oidc-admin-panel' }));
                return React.cloneElement(element, {}, ...children.slice(0, tabsIndex), extendedTabs,
                    ...(selected ? [h(Panel, { key: userId + session.token, userId, session })] : children.slice(tabsIndex + 1)));
            };
            return extend(tree);
        };
        module.exports = { ...module.exports, Component: Editor };
        require.r(module.exports);
    };
    const chunks = self.webpackChunk = self.webpackChunk || [];
    const wrapPush = push => function (...items) {
        for (const item of items) {
            const original = item[1]?.[63871];
            if (original && !original.oidcAdmin) {
                const wrapped = (module, exports, require) => installAdminEditor(module, exports, require, original);
                wrapped.oidcAdmin = true; item[1][63871] = wrapped;
            }
        }
        return push.apply(this, items);
    };
    let push = wrapPush(chunks.push);
    Object.defineProperty(chunks, 'push', { configurable: true, get: () => push, set: value => { push = wrapPush(value); } });
})();
