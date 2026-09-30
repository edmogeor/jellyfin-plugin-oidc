(() => {
    const oidcUrl = new URL('.', document.currentScript.src);
    const serverUrl = new URL('../', oidcUrl);
    let strings = {};
    let surface;
    let scheduled = false;
    let creation;
    let feedback;
    const text = (key, ...values) => (strings[key] || key).replace(/\{(\d+)\}/g, (_, index) => values[index]);
    const credentials = () => JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}').Servers?.find(server => server.AccessToken && (!server.ManualAddress || new URL(server.ManualAddress).origin === serverUrl.origin));
    const api = async (path, data) => {
        const server = credentials();
        if (!server) throw new Error(text('identitySessionExpired'));
        const response = await fetch(oidcUrl + 'identity/' + path, {
            method: data === undefined ? 'GET' : 'POST', cache: 'no-store',
            headers: { Authorization: `MediaBrowser Token="${server.AccessToken}"`, 'Content-Type': 'application/json' },
            body: data === undefined ? undefined : JSON.stringify(data),
        });
        if (!response.ok) {
            const error = await response.json().catch(() => ({}));
            throw new Error(error.Error || text(response.status === 401 || response.status === 403 ? 'identitySessionExpired' : 'identityConflict'));
        }
        return response.status === 204 ? null : response.json();
    };
    const element = (tag, content, className) => {
        const node = document.createElement(tag);
        if (content) node.textContent = content;
        if (className) node.className = className;
        return node;
    };
    const message = (host, content, error = false) => {
        let node = host.querySelector('[data-oidc-message]');
        if (!node) { node = element('p'); node.dataset.oidcMessage = 'true'; host.append(node); }
        node.setAttribute('role', error ? 'alert' : 'status'); node.textContent = content;
    };
    const button = (label, action, primary = false) => {
        const node = element('button', label, 'emby-button raised' + (primary ? ' button-submit' : ''));
        node.type = 'button';
        node.onclick = async () => {
            node.disabled = true;
            try { await action(); } catch (error) { message(node.closest('[data-oidc-surface]') || node.parentElement, error.message, true); }
            finally { if (node.isConnected) node.disabled = false; }
        };
        return node;
    };
    const confirm = (title, content) => new Promise(resolve => {
        const dialog = element('dialog', null, 'oidc-dialog');
        const heading = element('h2', title); heading.id = 'oidc-confirm-title';
        dialog.setAttribute('aria-labelledby', heading.id);
        dialog.append(heading, element('p', content));
        const actions = element('div', null, 'oidc-actions');
        actions.append(button(text('identityCancel'), () => { dialog.close('cancel'); }), button(text('identityConfirm'), () => { dialog.close('confirm'); }, true));
        dialog.append(actions);
        dialog.onclose = () => { const accepted = dialog.returnValue === 'confirm'; dialog.remove(); resolve(accepted); };
        document.body.append(dialog); dialog.showModal();
    });
    const style = element('style');
    style.textContent = `
        [data-oidc-surface] { margin-block: 1.5rem; max-width: 48rem; }
        #userProfilePage [data-oidc-surface] { margin-inline: auto; max-width: 54em; }
        .oidc-card { padding: 1.4rem; border: 1px solid var(--jf-palette-divider, #777); border-radius: .75rem; background: var(--jf-palette-background-paper, inherit); }
        .oidc-provider { display: flex; gap: .75rem; align-items: center; }
        .oidc-provider h2 { margin: 0; font-size: 1.3rem; }
        .oidc-card dl { display: grid; grid-template-columns: minmax(7rem, 1fr) minmax(0, 2fr); gap: .5rem 1rem; }
        .oidc-card dt { font-weight: 600; }
        .oidc-card dd { margin: 0; overflow-wrap: anywhere; }
        .oidc-actions { display: flex; flex-wrap: wrap; gap: .6rem; margin-top: 1rem; }
        .oidc-actions button { margin: 0; min-height: 44px; }
        .oidc-pending { margin-top: 2rem; }
        .oidc-dialog { color: var(--jf-palette-text-primary, #fff); background: var(--jf-palette-background-paper, #202020); border: 1px solid var(--jf-palette-divider, #777); border-radius: .75rem; padding: 1.5rem; max-width: min(32rem, calc(100vw - 5rem)); }
        .oidc-dialog::backdrop { background: #0008; }
        [data-oidc-surface] button:focus-visible, .oidc-dialog button:focus-visible { outline: 2px solid var(--jf-palette-primary-main, #00a4dc); outline-offset: 3px; }
        [data-oidc-hidden] { display: none !important; }
        [data-oidc-tab].Mui-selected { border-bottom: 2px solid var(--jf-palette-primary-main, #00a4dc); }
        [data-oidc-tab-scroller] { overflow-x: auto !important; }
        @media (max-width: 480px) { .oidc-card dl { grid-template-columns: 1fr; } .oidc-card dd { margin-bottom: .5rem; } }
    `;
    document.head.append(style);
    const setHidden = (node, hidden) => {
        if (!node) return;
        if (hidden) {
            if (node.contains(document.activeElement)) { document.activeElement.blur(); surface?.tab?.focus(); }
            if (!node.hasAttribute('data-oidc-hidden')) node.setAttribute('data-oidc-hidden', 'true');
        } else if (node.hasAttribute('data-oidc-hidden')) node.removeAttribute('data-oidc-hidden');
    };
    const claimFields = (host, prefix) => {
        const fields = element('div', null, 'oidc-pending');
        const selectContainer = element('div', null, 'selectContainer');
        const select = element('select', null, 'emby-select'); select.id = prefix + '-claim';
        const label = element('label', text('identityExpectedClaim'), 'selectLabel'); label.htmlFor = select.id;
        for (const [value, key] of [['VerifiedEmail', 'verifiedEmail'], ['PreferredUsername', 'preferredUsername']]) {
            const option = element('option', text(key)); option.value = value; select.append(option);
        }
        selectContainer.append(label, select);
        const inputContainer = element('div', null, 'inputContainer');
        const input = element('input', null, 'emby-input'); input.type = 'text'; input.id = prefix + '-value'; input.maxLength = 320;
        const inputLabel = element('label', text('identityExpectedValue'), 'inputLabel'); inputLabel.htmlFor = input.id;
        const help = element('p', text('identityPendingHelp'), 'fieldDescription'); help.id = prefix + '-help';
        input.setAttribute('aria-describedby', help.id); select.setAttribute('aria-describedby', help.id);
        inputContainer.append(inputLabel, input); fields.append(selectContainer, inputContainer, help); host.append(fields);
        return { fields, select, input };
    };
    const endpoint = state => state.admin ? 'users/' + state.userId : 'me';
    const refresh = async state => {
        const request = state.request = (state.request || 0) + 1;
        const status = await api(endpoint(state));
        if (surface !== state || !state.host.isConnected || request !== state.request) return;
        state.status = status;
        state.host.replaceChildren();
        const card = element('section', null, 'oidc-card');
        const provider = element('div', null, 'oidc-provider');
        provider.append(window.oidcProviderIcon(), element('h2', window.oidcProviderName || 'SSO'));
        card.append(provider, element('p', text(status.Linked ? 'identityLinked' : 'identityNotLinked', window.oidcProviderName || 'SSO')),
            element('p', text('identityJellyfinUser', status.UserName)));
        const details = element('dl');
        for (const [label, value] of [['identityLastEmail', status.Email], ['identityLastUsername', status.PreferredUsername],
            ...(state.admin && status.Linked ? [['identityIssuer', status.Issuer], ['identitySubject', status.Subject], ['identityOrigin', status.Origin]] : [])]) {
            details.append(element('dt', text(label)), element('dd', value || text('identityUnavailable')));
        }
        card.append(details);
        const actions = element('div', null, 'oidc-actions');
        if (status.Linked && (state.admin || status.AllowSelfService)) {
            const unlink = button(text('identityUnlink'), async () => {
                if (!await confirm(text('identityUnlinkTitle', status.UserName), text('identityUnlinkHelp') + ' ' + text(window.oidcPasswordLoginMode === 'DisableForAllUsers' ? 'identityAdminLockout' : 'identityPasswordRecovery'))) return;
                await api(endpoint(state) + '/unlink', { Revision: status.Revision });
                await refresh(state); message(state.host, text('identityUnlinkedSuccess'));
            });
            unlink.disabled = !status.CanUnlink; actions.append(unlink);
            if (!status.CanUnlink) card.append(element('p', text('identityUnlinkDisabled'), 'fieldDescription'));
        } else if (!status.Linked && (state.admin || status.AllowSelfService)) {
            actions.append(button(text(state.admin ? 'identityAdminLink' : 'identityLink'), async () => {
                const returnUrl = state.admin ? `/dashboard/users/${state.userId}/profile?oidcTab=1` : '/userprofile';
                const result = await api(endpoint(state) + '/start', { ReturnUrl: returnUrl, Revision: status.Revision });
                location.assign(result.Url);
            }, true));
        }
        card.append(actions); state.host.append(card);
        if (state.admin && !status.Linked) {
            if (status.HasOptOut) state.host.append(element('p', text('identityOptOutHelp'), 'fieldDescription'));
            const section = element('section'); section.append(element('h2', text('identityPendingHeading'))); state.host.append(section);
            const fields = claimFields(section, 'oidc-edit');
            fields.input.value = status.PendingMatch?.Value || ''; fields.select.value = status.PendingMatch?.Claim || 'VerifiedEmail';
            const pendingActions = element('div', null, 'oidc-actions');
            pendingActions.append(button(text('identitySavePending'), async () => {
                if (!fields.input.value.trim()) { fields.input.focus(); message(state.host, text('identityExpectedRequired'), true); return; }
                if (!await confirm(text('identitySavePending'), text('identityPendingConfirm', status.UserName, fields.select.options[fields.select.selectedIndex].textContent, fields.input.value))) return;
                await api(endpoint(state) + '/pending', { Claim: fields.select.value, Value: fields.input.value, Revision: status.Revision });
                await refresh(state); message(state.host, text('identityPendingSuccess'));
            }, true));
            if (status.PendingMatch) pendingActions.append(button(text('identityCancelPending'), async () => {
                if (!await confirm(text('identityCancelPending'), text('identityPendingConfirm', status.UserName, fields.select.options[fields.select.selectedIndex].textContent, status.PendingMatch.Value))) return;
                await api(endpoint(state) + '/pending', { Claim: fields.select.value, Value: '', Revision: status.Revision });
                await refresh(state); message(state.host, text('identityPendingCancelled'));
            }));
            fields.fields.append(pendingActions);
        }
        const params = new URLSearchParams(location.search);
        if (params.has('oidcLinkError')) {
            params.delete('oidcLinkError'); history.replaceState(null, '', location.pathname + (params.size ? '?' + params : '') + location.hash);
            message(state.host, text('identityLinkFailed'), true);
        }
        if (feedback && feedback.userId === state.userId) { message(state.host, feedback.text, feedback.error); feedback = null; }
    };
    const complete = async () => {
        const params = new URLSearchParams(location.search);
        const ticket = params.get('oidcLinkTicket');
        if (!ticket) return;
        params.delete('oidcLinkTicket'); history.replaceState(null, '', location.pathname + (params.size ? '?' + params : '') + location.hash);
        try {
            const identity = await api('review', { Ticket: ticket });
            if (identity.Administrator && !await confirm(text('identityAdminConfirmTitle'), text('identityAdminConfirm', identity.Email || identity.PreferredUsername || identity.Subject, identity.TargetName))) {
                await api('cancel', { Ticket: ticket });
                feedback = { userId: identity.TargetId.replaceAll('-', ''), text: text('identityLinkCancelled') };
            } else {
                await api('complete', { Ticket: ticket });
                feedback = { userId: identity.TargetId.replaceAll('-', ''), text: text('identityLinkedSuccess') };
            }
        } catch (error) { feedback = { userId: route()?.userId, text: error.message, error: true }; }
        if (surface) await refresh(surface).catch(error => message(surface.host, error.message, true));
    };
    const route = () => {
        const hash = location.hash.replace(/^#!?/, '');
        const admin = hash.match(/^\/dashboard\/users\/([a-f\d-]{32,36})\/(profile|access|parentalcontrol|password)(?:\?|$)/i);
        if (admin) return { admin: true, userId: admin[1].replaceAll('-', ''), key: 'admin-' + admin[1], root: document.querySelector('#usersEditPage'), hash };
        if (/^\/userprofile(?:\?|$)/.test(hash)) return { admin: false, userId: credentials()?.UserId?.replaceAll('-', ''), key: 'me', root: document.querySelector('#userProfilePage'), hash };
        return null;
    };
    const activateTab = state => {
        state.active = true; state.tab.setAttribute('aria-selected', 'true'); state.tab.tabIndex = 0; state.tab.classList.add('Mui-selected');
        setHidden(state.indicator, true);
        for (const tab of state.tabs.querySelectorAll('[role="tab"]:not([data-oidc-tab])')) { tab.setAttribute('aria-selected', 'false'); tab.tabIndex = -1; tab.classList.remove('Mui-selected'); }
        setHidden(state.native, true); setHidden(state.host, false);
    };
    const cleanup = () => {
        if (surface) {
            setHidden(surface.native, false); surface.host.remove(); surface.tab?.remove();
            setHidden(surface.indicator, false); surface.scroller?.removeAttribute('data-oidc-tab-scroller');
            surface.tabs?.removeEventListener('click', surface.nativeClick, true);
            surface.tabs?.removeEventListener('keydown', surface.keydown, true);
            surface = null;
        }
    };
    const update = () => {
        scheduled = false;
        const current = route();
        if (surface && (!current || current.key !== surface.key || current.root !== surface.root || !surface.host.isConnected || (surface.tab && !surface.tab.isConnected))) cleanup();
        const allDisabled = window.oidcPasswordLoginMode === 'DisableForAllUsers';
        document.querySelectorAll('#userProfilePage .passwordSection, #usersEditPage .updatePasswordForm, #usersEditPage .fldSelectPasswordResetProvider').forEach(node => setHidden(node, allDisabled));
        if (current?.admin && current.root) {
            const tabs = [...current.root.querySelectorAll('[role="tab"]:not([data-oidc-tab])')];
            setHidden(tabs[3], allDisabled);
        }
        if (current?.root && !surface && strings.identityLinked && credentials()) {
            const host = element('section'); host.dataset.oidcSurface = 'true'; host.setAttribute('aria-label', text('identityHeading'));
            const state = { ...current, host }; surface = state;
            if (current.admin) {
                const tabs = current.root.querySelector('[role="tablist"]');
                const tabsRoot = tabs?.closest('.MuiTabs-root');
                if (!tabsRoot?.nextElementSibling) { surface = null; return; }
                state.tabs = tabs; state.native = tabsRoot.nextElementSibling;
                state.indicator = tabsRoot.querySelector('.MuiTabs-indicator');
                state.scroller = tabs.parentElement; state.scroller.setAttribute('data-oidc-tab-scroller', 'true');
                const tab = tabs.querySelector('[role="tab"]').cloneNode(false); state.tab = tab;
                tab.removeAttribute('aria-selected'); tab.classList.remove('Mui-selected'); tab.dataset.oidcTab = 'true'; tab.textContent = 'OIDC'; tab.id = 'oidc-user-tab'; tab.tabIndex = -1;
                tab.setAttribute('aria-selected', 'false'); tab.setAttribute('aria-controls', 'oidc-user-panel');
                host.id = 'oidc-user-panel'; host.setAttribute('role', 'tabpanel'); host.setAttribute('aria-labelledby', tab.id);
                tabs.append(tab); tabsRoot.after(host); setHidden(host, true);
                tab.onclick = event => { event.preventDefault(); event.stopPropagation(); activateTab(state); void refresh(state).catch(error => message(host, error.message, true)); };
                state.nativeClick = event => {
                    if (event.target.closest('[data-oidc-tab]')) return;
                    if (!event.target.closest('[role="tab"]')) return;
                    state.active = false; tab.classList.remove('Mui-selected'); tab.setAttribute('aria-selected', 'false'); tab.tabIndex = -1;
                    for (const nativeTab of tabs.querySelectorAll('[role="tab"]:not([data-oidc-tab])')) {
                        const selected = nativeTab === event.target.closest('[role="tab"]');
                        nativeTab.setAttribute('aria-selected', String(selected)); nativeTab.tabIndex = selected ? 0 : -1; nativeTab.classList.toggle('Mui-selected', selected);
                    }
                    setHidden(state.native, false); setHidden(host, true);
                    setHidden(state.indicator, false);
                };
                state.keydown = event => {
                    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
                    const visibleTabs = [...tabs.querySelectorAll('[role="tab"]')].filter(node => !node.hasAttribute('data-oidc-hidden'));
                    const index = visibleTabs.indexOf(document.activeElement);
                    if (index < 0) return;
                    event.preventDefault(); event.stopImmediatePropagation();
                    const next = event.key === 'Home' ? 0 : event.key === 'End' ? visibleTabs.length - 1 : (index + (event.key === 'ArrowRight' ? 1 : -1) + visibleTabs.length) % visibleTabs.length;
                    visibleTabs[next].focus(); visibleTabs[next].click();
                };
                tabs.addEventListener('click', state.nativeClick, true); tabs.addEventListener('keydown', state.keydown, true);
                if (current.hash.includes('oidcTab=1') || feedback?.userId === current.userId || allDisabled && current.hash.includes('/password')) activateTab(state);
            } else current.root.querySelector('.padded-left')?.append(host);
            void refresh(state).catch(error => { if (surface === state) message(host, error.message, true); });
        }
        if (surface?.admin && surface.active) { setHidden(surface.native, true); setHidden(surface.host, false); }
        const newForm = document.querySelector('#newUserPage .newUserProfileForm');
        if (newForm && !newForm.querySelector('[data-oidc-new]') && strings.identityLinked) {
            const section = element('section'); section.dataset.oidcNew = 'true'; section.append(element('h2', text('identityNewHeading')));
            const fields = claimFields(section, 'oidc-new');
            newForm.querySelector('.folderAccessContainer')?.before(section);
            newForm.addEventListener('submit', () => { creation = fields.input.value.trim() ? { form: newForm, claim: fields.select.value, value: fields.input.value } : null; }, true);
        }
    };
    const schedule = () => { if (!scheduled) { scheduled = true; requestAnimationFrame(update); } };
    // Jellyfin 12's SDK submits user creation through XHR. Observe only the initiating form's successful request.
    const open = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (method, url, ...args) {
        if (method.toUpperCase() === 'POST' && new URL(url, location.href).pathname === new URL('Users/New', serverUrl).pathname) {
            const directive = creation; creation = null;
            if (directive?.form.isConnected) this.addEventListener('load', async () => {
                if (this.status < 200 || this.status >= 300) return;
                const user = typeof this.response === 'object' ? this.response : JSON.parse(this.responseText);
                if (!user?.Id) return;
                const userId = user.Id.replaceAll('-', '');
                try {
                    const status = await api('users/' + userId);
                    await api('users/' + userId + '/pending', { Claim: directive.claim, Value: directive.value, Revision: status.Revision });
                    feedback = { userId, text: text('identityPendingSuccess') };
                } catch {
                    feedback = { userId, text: text('identityPartialSuccess'), error: true };
                }
                location.hash = `/dashboard/users/${userId}/profile?oidcTab=1`; cleanup(); schedule();
            }, { once: true });
        }
        return open.call(this, method, url, ...args);
    };
    addEventListener('hashchange', () => { cleanup(); creation = null; schedule(); });
    addEventListener('oidcconfigured', () => { if (surface) void refresh(surface).catch(error => message(surface.host, error.message, true)); schedule(); });
    addEventListener('pageshow', schedule);
    new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
    const initialize = async () => {
        const english = await fetch(oidcUrl + 'strings/en-us').then(response => response.json());
        const locale = document.documentElement.lang.toLowerCase();
        let localized = {};
        for (const value of new Set([locale, locale.split('-')[0]])) {
            const response = await fetch(oidcUrl + 'strings/' + encodeURIComponent(value));
            if (response.ok) { localized = await response.json(); break; }
        }
        strings = { ...english, ...localized }; schedule(); await complete();
    };
    void initialize().catch(() => {});
})();
