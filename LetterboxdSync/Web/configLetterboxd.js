
export const pluginId = '99ec381d-a07c-4a0f-b245-ccb37eb14369';

export default function (view, params) {

    // Fix tab colors and spacing
    const style = document.createElement('style');
    style.textContent = `
        .emby-tab-button-active {
            background: #00a4dc !important;
            color: white !important;
        }
        .tabContent {
            margin-top: 1em;
        }
        .movieListHeader {
            background: #252525 !important;
            border-bottom: 2px solid #555 !important;
        }
    `;
    view.appendChild(style);

    view.addEventListener('viewshow', function (e) {
        const selectUsers = view.querySelector('#usersJellyfin');
        const libraryUserSelect = view.querySelector('#libraryUserSelect');
        const playlistSelect = view.querySelector('#selectPlaylist');
        
        selectUsers.innerHTML = '';
        libraryUserSelect.innerHTML = '';
        playlistSelect.innerHTML = '';

        // Load Playlists
        const playlistUrl = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdLog/GetPlaylists');
        ApiClient.getJSON(playlistUrl).then(playlists => {
            if (playlists.length > 0) {
                playlists.forEach(p => {
                    const opt = document.createElement('option');
                    opt.value = p.Id;
                    opt.textContent = p.Name;
                    playlistSelect.appendChild(opt);
                });
                // Default to first one (oldest/default)
                playlistSelect.selectedIndex = 0;
            } else {
                const opt = document.createElement('option');
                opt.textContent = 'No Playlists Found';
                playlistSelect.appendChild(opt);
            }
        });

        ApiClient.getUsers().then(users => {
            for (let user of users) {
                const option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                selectUsers.appendChild(option);
                libraryUserSelect.appendChild(option.cloneNode(true));
            }

            ApiClient.getCurrentUser().then(currentUser => {
                if (currentUser && currentUser.Id) {
                    const exists = Array.from(selectUsers.options).some(opt => opt.value === currentUser.Id);
                    if (exists) {
                        selectUsers.value = currentUser.Id;
                        libraryUserSelect.value = currentUser.Id;
                    }
                }
                loadAccountConfig(selectUsers.value);
                loadMovies(libraryUserSelect.value, playlistSelect.value);
            }).catch(() => {
                loadAccountConfig(selectUsers.value);
                loadMovies(libraryUserSelect.value, playlistSelect.value);
            });
        });
    });

    // Tab Logic
    const tabButtons = view.querySelectorAll('.emby-tab-button');
    const tabContents = view.querySelectorAll('.tabContent');

    tabButtons.forEach(btn => {
        btn.addEventListener('click', function() {
            const index = parseInt(this.getAttribute('data-index'));
            tabButtons.forEach(b => b.classList.remove('emby-tab-button-active'));
            this.classList.add('emby-tab-button-active');

            tabContents.forEach((content, i) => {
                if (i === index) {
                    content.classList.remove('hide');
                } else {
                    content.classList.add('hide');
                }
            });

            if (index === 1) {
                loadMovies(view.querySelector('#libraryUserSelect').value, view.querySelector('#selectPlaylist').value);
            }
        });
    });

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

    function loadMovies(userId, playlistId) {
        const body = view.querySelector('#movieListBody');
        body.innerHTML = '<div style="padding: 20px; text-align: center;">Loading movies...</div>';

        const url = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdLog/GetMovies', { userId: userId, playlistId: playlistId });
        ApiClient.getJSON(url).then(movies => {
            body.innerHTML = '';
            if (!movies || movies.length === 0) {
                body.innerHTML = '<div style="padding: 20px; text-align: center;">No movies found in library.</div>';
                return;
            }

            movies.forEach(movie => {
                const row = document.createElement('div');
                row.style = 'display: flex; padding: 10px; align-items: center; border-bottom: 1px solid #333;';
                
                const status = movie.IsPlayed ? (movie.HasIgnore ? '<span style="color: orange;">Watched (No Sync)</span>' : '<span style="color: #6fb03e;">Watched (Synced)</span>') : '<span style="color: #aaa;">Unwatched</span>';
                const actionLabel = movie.IsPlayed && movie.HasIgnore ? 'Reset Status' : 'Mark Watched (No Sync)';
                const actionClass = movie.IsPlayed && movie.HasIgnore ? 'button-flat' : 'button-accent';
                const playlistChecked = movie.IsInPlaylist ? 'checked' : '';

                row.innerHTML = `
                    <div style="flex: 2;">
                        <div style="font-weight: 500;">${movie.Name}</div>
                        <div style="font-size: 0.85em; color: #888;">${movie.Year || ''}</div>
                    </div>
                    <div style="flex: 1;">${status}</div>
                    <div style="flex: 1; text-align: center;">
                        <input is="emby-checkbox" type="checkbox" class="chkPlaylist" data-id="${movie.Id}" ${playlistChecked} />
                    </div>
                    <div style="flex: 1; text-align: right;">
                        <button is="emby-button" class="raised ${actionClass} btnMark" data-id="${movie.Id}" data-watched="${!(movie.IsPlayed && movie.HasIgnore)}">
                            <span>${actionLabel}</span>
                        </button>
                    </div>
                `;
                body.appendChild(row);
            });

            body.querySelectorAll('.btnMark').forEach(btn => {
                btn.addEventListener('click', function() {
                    markWatched(userId, playlistId, this.getAttribute('data-id'), this.getAttribute('data-watched') === 'true');
                });
            });

            body.querySelectorAll('.chkPlaylist').forEach(chk => {
                chk.addEventListener('change', function() {
                    togglePlaylist(userId, playlistId, this.getAttribute('data-id'), this.checked);
                });
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

    function markWatched(userId, playlistId, movieId, watched) {
        Dashboard.showLoadingMsg();
        const url = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdLog/MarkWatchedLocally');
        const data = { UserId: userId, MovieId: movieId, Watched: watched };

        ApiClient.ajax({
            type: 'POST',
            url: url,
            data: JSON.stringify(data),
            contentType: 'application/json'
        }).then(() => {
            Dashboard.hideLoadingMsg();
            loadMovies(userId, playlistId);
        }).catch(err => {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Error updating movie status');
        });
    }

    view.querySelector('#selectPlaylist').addEventListener('change', function(e) {
        loadMovies(view.querySelector('#libraryUserSelect').value, e.target.value);
    });

    view.querySelector('#libraryUserSelect').addEventListener('change', function(e) {
        loadMovies(e.target.value, view.querySelector('#selectPlaylist').value);
    });

    view.querySelector('#usersJellyfin').addEventListener('change', function (e) {
        loadAccountConfig(e.target.value);
    });

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
