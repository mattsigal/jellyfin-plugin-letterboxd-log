export const pluginId = '99ec381d-a07c-4a0f-b245-ccb37eb14369';

export default function (view, params) {

    // Inject styles
    const accentColor = '#aa5cc3';
    const accentHover = '#9147b0';
    const style = document.createElement('style');
    style.textContent = `
        .lbx-tabs {
            display: flex;
            gap: 0;
            margin-bottom: 1.5em;
            border-bottom: 2px solid ${accentColor};
        }
        .lbx-tab {
            padding: 10px 28px;
            border: none;
            cursor: pointer;
            font-size: 1em;
            font-weight: 600;
            background: transparent;
            color: #999;
            position: relative;
            transition: color 0.2s, background 0.2s;
            border-radius: 6px 6px 0 0;
        }
        .lbx-tab:hover {
            color: #ddd;
            background: rgba(170, 92, 195, 0.1);
        }
        .lbx-tab-active {
            color: #fff !important;
            background: ${accentColor} !important;
        }
        .movieListHeader {
            background: #252525 !important;
            border-bottom: 2px solid #555 !important;
            align-items: center;
        }
        .chkPlaylist {
            width: 20px !important;
            height: 20px !important;
            margin: 0 auto !important;
            display: block !important;
            cursor: pointer;
        }
        .paperList {
            background: #111;
        }
    `;
    view.appendChild(style);

    // Build tab buttons dynamically
    const tabDefs = [
        { id: 'SettingsTab', label: 'Settings' },
        { id: 'MediaLibraryTab', label: 'Media Library' },
        { id: 'HistoryTab', label: 'Letterboxd History' }
    ];
    const tabBar = view.querySelector('.lbx-tabs');
    tabDefs.forEach((def, i) => {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'lbx-tab' + (i === 0 ? ' lbx-tab-active' : '');
        btn.setAttribute('data-tab', def.id);
        btn.textContent = def.label;
        tabBar.appendChild(btn);
    });

    // Tab Logic — our own simple tabs, no Jellyfin framework dependency
    view.querySelectorAll('.lbx-tab').forEach(btn => {
        btn.addEventListener('click', function () {
            const targetId = this.getAttribute('data-tab');

            view.querySelectorAll('.lbx-tab').forEach(b => {
                b.classList.remove('lbx-tab-active');
            });
            this.classList.add('lbx-tab-active');

            view.querySelectorAll('.lbx-tabContent').forEach(c => {
                c.style.display = 'none';
            });
            const target = view.querySelector('#' + targetId);
            if (target) target.style.display = 'block';

            if (targetId === 'MediaLibraryTab') {
                loadMovies(view.querySelector('#libraryUserSelect').value, view.querySelector('#selectPlaylist').value);
            }
            if (targetId === 'HistoryTab') {
                loadHistory(view.querySelector('#historyUserSelect').value);
            }
        });
    });

    // Page init — loads users, playlists, and config
    let _initRunning = false;
    function initPage() {
        if (_initRunning) return;
        _initRunning = true;

        const selectUsers = view.querySelector('#usersJellyfin');
        const libraryUserSelect = view.querySelector('#libraryUserSelect');
        const playlistSelect = view.querySelector('#selectPlaylist');
        const historyUserSelect = view.querySelector('#historyUserSelect');

        selectUsers.innerHTML = '';
        libraryUserSelect.innerHTML = '';
        playlistSelect.innerHTML = '';
        historyUserSelect.innerHTML = '';

        // Load Playlists
        const playlistUrl = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdLog/GetPlaylists');
        ApiClient.getJSON(playlistUrl).then(playlists => {
            playlistSelect.innerHTML = '';
            if (playlists.length > 0) {
                let huxIndex = -1;
                playlists.forEach((p, index) => {
                    const opt = document.createElement('option');
                    opt.value = p.Id;
                    opt.textContent = p.Name;
                    if (p.Name.toLowerCase().includes('hux list')) huxIndex = index;
                    playlistSelect.appendChild(opt);
                });
                playlistSelect.selectedIndex = (huxIndex !== -1) ? huxIndex : 0;
            } else {
                const opt = document.createElement('option');
                opt.textContent = 'No Playlists Found';
                playlistSelect.appendChild(opt);
            }

            if (libraryUserSelect.value) {
                loadMovies(libraryUserSelect.value, playlistSelect.value);
            }
        });

        ApiClient.getUsers().then(users => {
            for (let user of users) {
                const option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                selectUsers.appendChild(option);
                libraryUserSelect.appendChild(option.cloneNode(true));
                historyUserSelect.appendChild(option.cloneNode(true));
            }

            ApiClient.getCurrentUser().then(currentUser => {
                if (currentUser && currentUser.Id) {
                    const exists = Array.from(selectUsers.options).some(opt => opt.value === currentUser.Id);
                    if (exists) {
                        selectUsers.value = currentUser.Id;
                        libraryUserSelect.value = currentUser.Id;
                        historyUserSelect.value = currentUser.Id;
                    }
                }
                loadAccountConfig(selectUsers.value);
            }).catch(() => {
                loadAccountConfig(selectUsers.value);
            });
        });
    }

    view.addEventListener('viewshow', initPage);
    // Also run immediately — controller may load after viewshow has already fired
    initPage();

    function loadAccountConfig(userSelectedId) {
        ApiClient.getPluginConfiguration(pluginId).then(config => {
            let configUserFilter = (config.Accounts || []).find(item => item.UserJellyfin == userSelectedId);

            if (configUserFilter) {
                view.querySelector('#username').value = configUserFilter.UserLetterboxd || '';
                view.querySelector('#password').value = configUserFilter.PasswordLetterboxd || '';
                view.querySelector('#enable').checked = configUserFilter.Enable;
                view.querySelector('#sendfavorite').checked = configUserFilter.SendFavorite;
                view.querySelector('#enabledatefilter').checked = configUserFilter.EnableDateFilter || false;
                view.querySelector('#datefilterdays').value = configUserFilter.DateFilterDays || 7;
                view.querySelector('#timezoneoffset').value = configUserFilter.TimezoneOffset || 0;
                view.querySelector('#cookiesraw').value = configUserFilter.CookiesRaw || '';
                view.querySelector('#cookiesuseragent').value = configUserFilter.CookiesUserAgent || '';
            } else {
                ['#username', '#password', '#datefilterdays', '#timezoneoffset', '#cookiesraw', '#cookiesuseragent'].forEach(id => {
                    const el = view.querySelector(id);
                    if (el) el.value = id.includes('days') ? 7 : (id.includes('offset') ? 0 : '');
                });
                ['#enable', '#sendfavorite', '#enabledatefilter'].forEach(id => {
                    const el = view.querySelector(id);
                    if (el) el.checked = false;
                });
            }
        });
    }

    // Store loaded movies for client-side filtering
    let _allMovies = [];
    let _currentUserId = '';
    let _currentPlaylistId = '';

    function loadMovies(userId, playlistId) {
        if (!userId) return;
        _currentUserId = userId;
        _currentPlaylistId = playlistId;

        const body = view.querySelector('#movieListBody');
        body.innerHTML = '<div style="padding: 20px; text-align: center;">Loading movies...</div>';

        const params = { userId: userId };
        if (playlistId && playlistId.length > 5) {
            params.playlistId = playlistId;
        }

        const url = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdLog/GetMovies', params);
        ApiClient.getJSON(url).then(movies => {
            _allMovies = movies || [];
            view.querySelector('#filterStatus').value = 'all';
            renderMovies();
        });
    }

    function renderMovies() {
        const body = view.querySelector('#movieListBody');
        const filter = view.querySelector('#filterStatus').value;
        body.innerHTML = '';

        let movies = _allMovies;
        if (filter === 'unwatched') {
            movies = movies.filter(m => !m.IsPlayed);
        } else if (filter === 'watched-nosync') {
            movies = movies.filter(m => m.IsPlayed && m.HasIgnore);
        } else if (filter === 'watched-synced') {
            movies = movies.filter(m => m.IsPlayed && !m.HasIgnore);
        }

        if (movies.length === 0) {
            body.innerHTML = '<div style="padding: 20px; text-align: center;">No movies found.</div>';
            return;
        }

        const serverBase = ApiClient.serverAddress();

        movies.forEach(movie => {
            const row = document.createElement('div');
            row.className = 'movieRow';
            row.style = 'display: flex; padding: 10px; align-items: center; border-bottom: 1px solid #333; min-height: 50px;';

            const status = movie.IsPlayed ? (movie.HasIgnore ? '<span style="color: orange;">Watched (No Sync)</span>' : '<span style="color: #6fb03e;">Watched (Synced)</span>') : '<span style="color: #aaa;">Unwatched</span>';
            const actionLabel = movie.IsPlayed && movie.HasIgnore ? 'Reset Status' : 'Mark Watched (No Sync)';
            const actionClass = movie.IsPlayed && movie.HasIgnore ? 'button-flat' : 'button-accent';
            const playlistChecked = movie.IsInPlaylist ? 'checked' : '';
            const allPlaylistsText = (movie.AllPlaylists && movie.AllPlaylists.length > 0) ? movie.AllPlaylists.join(', ') : '<span style="color: #555;">—</span>';
            const jellyfinUrl = serverBase + '/web/index.html#!/details?id=' + movie.Id;

            row.innerHTML = `
                <div style="flex: 2;">
                    <div style="font-weight: 500;"><a href="${jellyfinUrl}" target="_blank" style="color: #aa5cc3; text-decoration: none;">${movie.Name}</a></div>
                    <div style="font-size: 0.85em; color: #888;">${movie.Year || ''}</div>
                </div>
                <div style="flex: 1;" class="statusText">${status}</div>
                <div style="flex: 1; font-size: 0.85em; color: #aaa;">${allPlaylistsText}</div>
                <div style="flex: 1; text-align: center;">
                    <input type="checkbox" class="chkPlaylist" data-id="${movie.Id}" ${playlistChecked} />
                </div>
                <div style="flex: 1; text-align: right;">
                    <button is="emby-button" class="raised ${actionClass} btnMark" style="margin: 0;" data-id="${movie.Id}" data-watched="${!(movie.IsPlayed && movie.HasIgnore)}">
                        <span>${actionLabel}</span>
                    </button>
                </div>
            `;
            body.appendChild(row);
        });

        body.querySelectorAll('.btnMark').forEach(btn => {
            btn.addEventListener('click', function () {
                markWatched(this, _currentUserId, _currentPlaylistId, this.getAttribute('data-id'), this.getAttribute('data-watched') === 'true');
            });
        });

        body.querySelectorAll('.chkPlaylist').forEach(chk => {
            chk.addEventListener('change', function () {
                togglePlaylist(_currentUserId, _currentPlaylistId, this.getAttribute('data-id'), this.checked);
            });
        });
    }

    function togglePlaylist(userId, playlistId, movieId, inPlaylist) {
        const url = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdLog/TogglePlaylist');
        const data = { UserId: userId, PlaylistId: playlistId, MovieId: movieId, InPlaylist: inPlaylist };

        ApiClient.ajax({
            type: 'POST',
            url: url,
            data: JSON.stringify(data),
            contentType: 'application/json'
        }).catch(err => {
            Dashboard.alert('Error updating playlist');
        });
    }

    function markWatched(btn, userId, playlistId, movieId, watched) {
        const url = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdLog/MarkWatchedLocally');
        const data = { UserId: userId, MovieId: movieId, Watched: watched };

        const row = btn.closest('.movieRow');
        if (!row) return;
        const statusText = row.querySelector('.statusText');
        const btnText = btn.querySelector('span');

        if (watched) {
            statusText.innerHTML = '<span style="color: orange;">Watched (No Sync)</span>';
            btnText.textContent = 'Reset Status';
            btn.classList.remove('button-accent');
            btn.classList.add('button-flat');
            btn.setAttribute('data-watched', 'false');
        } else {
            statusText.innerHTML = '<span style="color: #aaa;">Unwatched</span>';
            btnText.textContent = 'Mark Watched (No Sync)';
            btn.classList.remove('button-flat');
            btn.classList.add('button-accent');
            btn.setAttribute('data-watched', 'true');
        }

        ApiClient.ajax({
            type: 'POST',
            url: url,
            data: JSON.stringify(data),
            contentType: 'application/json'
        }).catch(err => {
            Dashboard.alert('Error updating movie status');
        });
    }

    view.querySelector('#selectPlaylist').addEventListener('change', function (e) {
        loadMovies(view.querySelector('#libraryUserSelect').value, e.target.value);
    });

    view.querySelector('#libraryUserSelect').addEventListener('change', function (e) {
        loadMovies(e.target.value, view.querySelector('#selectPlaylist').value);
    });

    view.querySelector('#filterStatus').addEventListener('change', function () {
        renderMovies();
    });

    view.querySelector('#historyUserSelect').addEventListener('change', function (e) {
        loadHistory(e.target.value);
    });

    view.querySelector('#usersJellyfin').addEventListener('change', function (e) {
        loadAccountConfig(e.target.value);
    });

    // Cache history per user to avoid repeated slow fetches
    let _historyCache = {};

    function formatDateLogged(dateStr) {
        if (!dateStr) return '';
        // If it has a T (ISO timestamp), format nicely
        if (dateStr.includes('T')) {
            const d = new Date(dateStr);
            if (!isNaN(d)) {
                return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
                    + ' ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
            }
        }
        // Otherwise just return the date string as-is (e.g. "2026-03-24")
        return dateStr;
    }

    function loadHistory(userId) {
        if (!userId) return;

        const body = view.querySelector('#historyListBody');

        // Use cache if available
        if (_historyCache[userId]) {
            renderHistory(_historyCache[userId]);
            return;
        }

        body.innerHTML = '<div style="padding: 20px; text-align: center;">Loading history...</div>';

        const url = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdLog/GetHistory', { userId: userId });
        ApiClient.getJSON(url).then(entries => {
            _historyCache[userId] = entries || [];
            renderHistory(_historyCache[userId]);
        }).catch(() => {
            body.innerHTML = '<div style="padding: 20px; text-align: center;">Error loading history.</div>';
        });
    }

    function renderHistory(entries) {
        const body = view.querySelector('#historyListBody');
        body.innerHTML = '';
        if (!entries || entries.length === 0) {
            body.innerHTML = '<div style="padding: 20px; text-align: center;">No Letterboxd history found.</div>';
            return;
        }

        const serverBase = ApiClient.serverAddress();

        entries.forEach(entry => {
            const row = document.createElement('div');
            row.style = 'display: flex; padding: 10px; align-items: center; border-bottom: 1px solid #333; min-height: 50px;';

            const jellyfinUrl = serverBase + '/web/index.html#!/details?id=' + entry.Id;
            const lbxLink = entry.LetterboxdUrl
                ? `<a href="${entry.LetterboxdUrl}" target="_blank" style="color: #00c030; text-decoration: none;">Open Diary Entry</a>`
                : '<span style="color: #555;">—</span>';

            row.innerHTML = `
                <div style="flex: 2;">
                    <div style="font-weight: 500;"><a href="${jellyfinUrl}" target="_blank" style="color: #aa5cc3; text-decoration: none;">${entry.Name}</a></div>
                    <div style="font-size: 0.85em; color: #888;">${entry.Year || ''}</div>
                </div>
                <div style="flex: 1;">${formatDateLogged(entry.DateLogged)}</div>
                <div style="flex: 1; text-align: right;">${lbxLink}</div>
            `;
            body.appendChild(row);
        });
    }

    view.querySelector('#LetterboxdLogConfigForm').addEventListener('submit', function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        const userSelectedId = view.querySelector('#usersJellyfin').value;

        ApiClient.getPluginConfiguration(pluginId).then(config => {
            let AccountsUpdate = (config.Accounts || []).filter(account => account.UserJellyfin != userSelectedId);
            let configUser = {
                UserJellyfin: userSelectedId,
                UserLetterboxd: view.querySelector('#username').value,
                PasswordLetterboxd: view.querySelector('#password').value,
                Enable: view.querySelector('#enable').checked,
                SendFavorite: view.querySelector('#sendfavorite').checked,
                EnableDateFilter: view.querySelector('#enabledatefilter').checked,
                DateFilterDays: parseInt(view.querySelector('#datefilterdays').value) || 7,
                TimezoneOffset: parseInt(view.querySelector('#timezoneoffset').value) || 0,
                CookiesRaw: view.querySelector('#cookiesraw').value,
                CookiesUserAgent: view.querySelector('#cookiesuseragent').value
            };

            const data = JSON.stringify(configUser);
            const url = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdLog/Authenticate');

            if (!configUser.Enable) {
                AccountsUpdate.push(configUser);
                config.Accounts = AccountsUpdate;
                ApiClient.updatePluginConfiguration(pluginId, config).then(result => {
                    Dashboard.hideLoadingMsg();
                    Dashboard.processPluginConfigurationUpdateResult(result);
                });
            } else {
                ApiClient.ajax({ type: 'POST', url, data, contentType: 'application/json' }).then(response => {
                    AccountsUpdate.push(configUser);
                    config.Accounts = AccountsUpdate;
                    ApiClient.updatePluginConfiguration(pluginId, config).then(result => {
                        Dashboard.hideLoadingMsg();
                        Dashboard.processPluginConfigurationUpdateResult(result);
                    });
                }).catch(response => {
                    Dashboard.hideLoadingMsg();
                    response.json?.().then(res => Dashboard.processErrorResponse({ statusText: `${response.statusText || 'Error'} - ${res.Message || 'Unknown'}` }))
                        .catch(() => Dashboard.processErrorResponse({ statusText: response.statusText || 'Authentication failed' }));
                });
            }
        });
    });
}
