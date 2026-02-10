(function () {
    'use strict';

    var MENU_ID = 'letterboxdSyncMenuSection';
    var DIALOG_ID = 'letterboxdSyncDialog';

    // ===== Sidebar injection =====
    function injectSidebarEntry() {
        if (document.getElementById(MENU_ID)) {
            return;
        }

        var scrollContainer = document.querySelector('.mainDrawer-scrollContainer');
        if (!scrollContainer) {
            return;
        }

        var section = document.createElement('div');
        section.id = MENU_ID;
        section.className = 'navMenuSection';

        var header = document.createElement('h3');
        header.className = 'sidebarHeader';
        header.textContent = 'Letterboxd';
        section.appendChild(header);

        var link = document.createElement('a');
        link.setAttribute('is', 'emby-linkbutton');
        link.className = 'navMenuOption lnkMediaFolder emby-button';
        link.href = '#';
        link.addEventListener('click', function (e) {
            e.preventDefault();
            closeSidebar();
            setTimeout(openDialog, 400);
        });
        link.innerHTML = '<span class="material-icons navMenuOptionIcon" aria-hidden="true">movie_filter</span>' +
            '<span class="navMenuOptionText">Letterboxd Sync</span>';
        section.appendChild(link);

        var adminSection = scrollContainer.querySelector('.adminMenuOptions');
        if (adminSection) {
            adminSection.parentNode.insertBefore(section, adminSection);
        } else {
            scrollContainer.appendChild(section);
        }
    }

    function closeSidebar() {
        var drawer = document.querySelector('.mainDrawer');
        if (drawer) {
            drawer.classList.remove('opened');
            drawer.style.transform = '';
        }
        var backdrop = document.querySelector('.mainDrawer-backdrop');
        if (backdrop) {
            backdrop.classList.remove('opened');
            backdrop.style.display = 'none';
        }
        document.body.classList.remove('bodyWithPopupOpen');
    }

    // ===== Dialog =====
    function openDialog() {
        var existing = document.getElementById(DIALOG_ID);
        if (existing) {
            existing.remove();
        }

        var overlay = document.createElement('div');
        overlay.id = DIALOG_ID;
        overlay.style.cssText = 'position:fixed;top:0;left:0;right:0;bottom:0;z-index:999;display:flex;align-items:center;justify-content:center;';

        // Backdrop
        var backdrop = document.createElement('div');
        backdrop.style.cssText = 'position:absolute;top:0;left:0;right:0;bottom:0;background:rgba(0,0,0,0.7);';
        backdrop.addEventListener('click', function () { closeDialog(); });
        overlay.appendChild(backdrop);

        // Dialog panel
        var panel = document.createElement('div');
        panel.style.cssText = [
            'position:relative;',
            'background:var(--theme-card-background, #1c1c1e);',
            'color:var(--theme-text-color, #fff);',
            'border-radius:8px;',
            'width:90%;',
            'max-width:600px;',
            'max-height:85vh;',
            'overflow-y:auto;',
            'padding:1.5em 2em;',
            'box-shadow:0 8px 32px rgba(0,0,0,0.5);'
        ].join('');

        // Header bar
        var headerBar = document.createElement('div');
        headerBar.style.cssText = 'display:flex;align-items:center;justify-content:space-between;margin-bottom:1em;';

        var title = document.createElement('h2');
        title.style.cssText = 'margin:0;font-size:1.4em;';
        title.textContent = 'Letterboxd Sync';
        headerBar.appendChild(title);

        var closeBtn = document.createElement('button');
        closeBtn.type = 'button';
        closeBtn.className = 'paper-icon-button-light';
        closeBtn.style.cssText = 'background:none;border:none;color:inherit;cursor:pointer;font-size:1.4em;padding:4px;';
        closeBtn.innerHTML = '<span class="material-icons">close</span>';
        closeBtn.addEventListener('click', function () { closeDialog(); });
        headerBar.appendChild(closeBtn);

        panel.appendChild(headerBar);

        // Form content
        var formHtml = [
            '<form id="LetterboxdSyncUserConfigForm">',
            '  <h3 style="margin-top:0;">Letterboxd Login</h3>',
            '  <div class="inputContainer">',
            '    <label class="inputLabel" for="lbxd-username">Letterboxd Username</label>',
            '    <input type="text" id="lbxd-username" class="emby-input" />',
            '  </div>',
            '  <div class="inputContainer">',
            '    <label class="inputLabel" for="lbxd-password">Letterboxd Password</label>',
            '    <input type="password" id="lbxd-password" class="emby-input" />',
            '  </div>',
            '  <div class="inputContainer">',
            '    <label class="inputLabel" for="lbxd-cookies">Raw Cookies</label>',
            '    <textarea id="lbxd-cookies" class="emby-textarea" rows="3" style="width:100%;box-sizing:border-box;"></textarea>',
            '    <div class="fieldDescription">If you get 403 errors, paste cookie headers from your browser after visiting letterboxd.com.</div>',
            '  </div>',
            '  <h3>Options</h3>',
            '  <div class="checkboxContainer checkboxContainer-withDescription">',
            '    <label class="emby-checkbox-label">',
            '      <input is="emby-checkbox" type="checkbox" id="lbxd-enable" data-embycheckbox="true" class="emby-checkbox" />',
            '      <span class="checkboxLabel">Enable sync</span>',
            '      <span class="checkboxOutline"><span class="material-icons checkboxIcon checkboxIcon-checked check" aria-hidden="true"></span><span class="material-icons checkboxIcon checkboxIcon-unchecked" aria-hidden="true"></span></span>',
            '    </label>',
            '    <div class="fieldDescription checkboxFieldDescription">Synchronizes the films watched by this user</div>',
            '  </div>',
            '  <div class="checkboxContainer checkboxContainer-withDescription">',
            '    <label class="emby-checkbox-label">',
            '      <input is="emby-checkbox" type="checkbox" id="lbxd-sendfavorite" data-embycheckbox="true" class="emby-checkbox" />',
            '      <span class="checkboxLabel">Send Favorites</span>',
            '      <span class="checkboxOutline"><span class="material-icons checkboxIcon checkboxIcon-checked check" aria-hidden="true"></span><span class="material-icons checkboxIcon checkboxIcon-unchecked" aria-hidden="true"></span></span>',
            '    </label>',
            '  </div>',
            '  <h3>Date Filtering</h3>',
            '  <div class="checkboxContainer checkboxContainer-withDescription">',
            '    <label class="emby-checkbox-label">',
            '      <input is="emby-checkbox" type="checkbox" id="lbxd-datefilter" data-embycheckbox="true" class="emby-checkbox" />',
            '      <span class="checkboxLabel">Enable Date Filtering</span>',
            '      <span class="checkboxOutline"><span class="material-icons checkboxIcon checkboxIcon-checked check" aria-hidden="true"></span><span class="material-icons checkboxIcon checkboxIcon-unchecked" aria-hidden="true"></span></span>',
            '    </label>',
            '    <div class="fieldDescription checkboxFieldDescription">Only sync movies played within the specified number of days</div>',
            '  </div>',
            '  <div class="inputContainer">',
            '    <label class="inputLabel" for="lbxd-datedays">Days to look back</label>',
            '    <input type="number" id="lbxd-datedays" class="emby-input" min="1" max="365" />',
            '  </div>',
            '  <h3>Watchlist Sync</h3>',
            '  <div class="fieldDescription" style="margin-bottom:0.5em;">Add Letterboxd usernames whose public watchlists to sync as playlists.</div>',
            '  <div id="lbxd-watchlist-container"></div>',
            '  <button type="button" id="lbxd-add-watchlist" class="raised emby-button" style="margin:0.5em 0;">',
            '    <span>Add Watchlist</span>',
            '  </button>',
            '  <br/><br/>',
            '  <button type="submit" class="raised button-submit block emby-button">',
            '    <span>Save</span>',
            '  </button>',
            '</form>'
        ].join('\n');

        var formContainer = document.createElement('div');
        formContainer.innerHTML = formHtml;
        panel.appendChild(formContainer);

        overlay.appendChild(panel);
        document.body.appendChild(overlay);

        // Close on Escape
        overlay._onKeyDown = function (e) {
            if (e.key === 'Escape') {
                closeDialog();
            }
        };
        document.addEventListener('keydown', overlay._onKeyDown);

        loadConfig(panel);
        setupForm(panel);
    }

    function closeDialog() {
        var dialog = document.getElementById(DIALOG_ID);
        if (dialog) {
            if (dialog._onKeyDown) {
                document.removeEventListener('keydown', dialog._onKeyDown);
            }
            dialog.remove();
        }
    }

    // ===== Config loading =====
    function loadConfig(view) {
        var url = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdSync/UserConfig');
        ApiClient.ajax({ type: 'GET', url: url, dataType: 'json' }).then(function (account) {
            view.querySelector('#lbxd-username').value = account.UserLetterboxd || '';
            view.querySelector('#lbxd-password').value = account.PasswordLetterboxd || '';
            view.querySelector('#lbxd-cookies').value = account.CookiesRaw || '';
            view.querySelector('#lbxd-enable').checked = account.Enable || false;
            view.querySelector('#lbxd-sendfavorite').checked = account.SendFavorite || false;
            view.querySelector('#lbxd-datefilter').checked = account.EnableDateFilter || false;
            view.querySelector('#lbxd-datedays').value = account.DateFilterDays || 7;

            var container = view.querySelector('#lbxd-watchlist-container');
            container.innerHTML = '';
            var usernames = account.WatchlistUsernames || [];
            for (var i = 0; i < usernames.length; i++) {
                addWatchlistEntry(container, usernames[i]);
            }
        });
    }

    function addWatchlistEntry(container, value) {
        var row = document.createElement('div');
        row.style.cssText = 'display:flex;align-items:center;gap:10px;margin-bottom:0.5em;';

        var input = document.createElement('input');
        input.type = 'text';
        input.className = 'emby-input watchlist-username';
        input.placeholder = 'Letterboxd Username';
        input.value = value || '';
        input.style.flex = '1';

        var removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'raised emby-button';
        removeBtn.textContent = 'Remove';
        removeBtn.addEventListener('click', function () {
            row.remove();
        });

        row.appendChild(input);
        row.appendChild(removeBtn);
        container.appendChild(row);
    }

    // ===== Form logic =====
    function setupForm(view) {
        view.querySelector('#lbxd-add-watchlist').addEventListener('click', function () {
            addWatchlistEntry(view.querySelector('#lbxd-watchlist-container'), '');
        });

        view.querySelector('#LetterboxdSyncUserConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();

            var configUser = {
                UserLetterboxd: view.querySelector('#lbxd-username').value,
                PasswordLetterboxd: view.querySelector('#lbxd-password').value,
                CookiesRaw: view.querySelector('#lbxd-cookies').value,
                Enable: view.querySelector('#lbxd-enable').checked,
                SendFavorite: view.querySelector('#lbxd-sendfavorite').checked,
                EnableDateFilter: view.querySelector('#lbxd-datefilter').checked,
                DateFilterDays: parseInt(view.querySelector('#lbxd-datedays').value) || 7,
                WatchlistUsernames: []
            };

            var inputs = view.querySelectorAll('.watchlist-username');
            for (var i = 0; i < inputs.length; i++) {
                var val = inputs[i].value.trim();
                if (val) {
                    configUser.WatchlistUsernames.push(val);
                }
            }

            var data = JSON.stringify(configUser);

            function saveConfig() {
                var saveUrl = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdSync/UserConfig');
                ApiClient.ajax({ type: 'POST', url: saveUrl, data: data, contentType: 'application/json' }).then(function () {
                    Dashboard.alert('Settings saved.');
                    closeDialog();
                }).catch(function () {
                    Dashboard.alert('Error saving settings.');
                });
            }

            if (!configUser.Enable) {
                saveConfig();
            } else {
                var authUrl = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdSync/UserAuthenticate');
                ApiClient.ajax({ type: 'POST', url: authUrl, data: data, contentType: 'application/json' }).then(function () {
                    saveConfig();
                }).catch(function (response) {
                    if (response && response.json) {
                        response.json().then(function (res) {
                            Dashboard.alert('Authentication failed: ' + res.Message);
                        });
                    } else {
                        Dashboard.alert('Authentication failed.');
                    }
                });
            }
        });
    }

    // ===== Init =====
    var observer = new MutationObserver(function () {
        injectSidebarEntry();
    });

    observer.observe(document.body, {
        childList: true,
        subtree: true
    });

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', injectSidebarEntry);
    } else {
        injectSidebarEntry();
    }
})();
