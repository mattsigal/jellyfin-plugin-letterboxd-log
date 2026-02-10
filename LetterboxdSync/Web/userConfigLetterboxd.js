
export default function (view, params) {

    function addWatchlistEntry(container, value) {
        var row = document.createElement('div');
        row.className = 'inputContainer';
        row.style.display = 'flex';
        row.style.alignItems = 'center';
        row.style.gap = '10px';

        var input = document.createElement('input');
        input.setAttribute('is', 'emby-input');
        input.type = 'text';
        input.className = 'watchlist-entry';
        input.setAttribute('label', 'Watchlist link or username');
        input.setAttribute('autocomplete', 'off');
        input.value = value || '';
        input.style.flex = '1';

        var removeBtn = document.createElement('button');
        removeBtn.setAttribute('is', 'emby-button');
        removeBtn.type = 'button';
        removeBtn.className = 'raised';
        removeBtn.textContent = 'Remove';
        removeBtn.addEventListener('click', function () {
            row.remove();
        });

        row.appendChild(input);
        row.appendChild(removeBtn);
        container.appendChild(row);
    }

    view.addEventListener('viewshow', function (e) {

        var container = view.querySelector('#watchlistContainer');

        var url = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdSync/UserConfig');
        ApiClient.ajax({ type: 'GET', url: url, dataType: 'json' }).then(function (account) {
            view.querySelector('#username').value = account.UserLetterboxd || '';
            view.querySelector('#password').value = account.PasswordLetterboxd || '';
            view.querySelector('#cookiesraw').value = account.CookiesRaw || '';
            view.querySelector('#enable').checked = account.Enable || false;
            view.querySelector('#sendfavorite').checked = account.SendFavorite || false;
            view.querySelector('#enabledatefilter').checked = account.EnableDateFilter || false;
            view.querySelector('#datefilterdays').value = account.DateFilterDays || 7;

            container.innerHTML = '';
            var usernames = account.WatchlistUsernames || [];
            for (var i = 0; i < usernames.length; i++) {
                addWatchlistEntry(container, usernames[i]);
            }
        });
    });

    view.querySelector('#addWatchlistBtn').addEventListener('click', function () {
        var container = view.querySelector('#watchlistContainer');
        addWatchlistEntry(container, '');
    });

    view.querySelector('#LetterboxdSyncUserConfigForm').addEventListener('submit', function (e) {

        e.preventDefault();

        Dashboard.showLoadingMsg();

        var configUser = {};
        configUser.UserLetterboxd = view.querySelector('#username').value;
        configUser.PasswordLetterboxd = view.querySelector('#password').value;
        configUser.CookiesRaw = view.querySelector('#cookiesraw').value;
        configUser.Enable = view.querySelector('#enable').checked;
        configUser.SendFavorite = view.querySelector('#sendfavorite').checked;
        configUser.EnableDateFilter = view.querySelector('#enabledatefilter').checked;
        configUser.DateFilterDays = parseInt(view.querySelector('#datefilterdays').value) || 7;

        var watchlistInputs = view.querySelectorAll('.watchlist-entry');
        var watchlistUsernames = [];
        for (var i = 0; i < watchlistInputs.length; i++) {
            var val = watchlistInputs[i].value.trim();
            if (val) {
                watchlistUsernames.push(val);
            }
        }
        configUser.WatchlistUsernames = watchlistUsernames;

        var data = JSON.stringify(configUser);

        function saveConfig() {
            var saveUrl = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdSync/UserConfig');
            ApiClient.ajax({ type: 'POST', url: saveUrl, data: data, contentType: 'application/json' }).then(function () {
                Dashboard.hideLoadingMsg();
                Dashboard.alert('Settings saved.');
            }).catch(function () {
                Dashboard.hideLoadingMsg();
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
                response.json().then(function (res) {
                    Dashboard.hideLoadingMsg();
                    Dashboard.processErrorResponse({ statusText: response.statusText + ' - ' + res.Message });
                });
            });
        }
    });
}
