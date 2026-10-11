/**
 * Submarine In-Player On-Demand Subtitle Search & Live Activation Client
 * Works across jellyfin-web, Jellyfin Media Player (JMP), and Mobile Web wrappers.
 */
(function () {
    'use strict';

    if (window.SubmarinePlayer && window.SubmarinePlayer.__initialized) {
        return;
    }

    var LANGUAGES = [
        { code: 'eng', label: 'English (eng)' },
        { code: 'spa', label: 'Spanish (spa)' },
        { code: 'fre', label: 'French (fre)' },
        { code: 'ger', label: 'German (ger)' },
        { code: 'ita', label: 'Italian (ita)' },
        { code: 'por', label: 'Portuguese (por)' },
        { code: 'jpn', label: 'Japanese (jpn)' },
        { code: 'kor', label: 'Korean (kor)' },
        { code: 'chi', label: 'Chinese (chi)' },
        { code: 'ara', label: 'Arabic (ara)' },
        { code: 'rus', label: 'Russian (rus)' },
        { code: 'dut', label: 'Dutch (dut)' },
        { code: 'pol', label: 'Polish (pol)' },
        { code: 'swe', label: 'Swedish (swe)' },
        { code: 'tur', label: 'Turkish (tur)' },
        { code: 'hin', label: 'Hindi (hin)' }
    ];

    var FORMATS = ['srt', 'vtt', 'ass', 'ssa', 'sub', 'ttml'];

    var state = {
        activeItemId: null,
        activeMediaSourceId: null,
        selectedLanguage: 'eng',
        selectedFormat: 'vtt',
        searchQuery: '',
        offsetMs: 0,
        persistToLibrary: true,
        results: [],
        loading: false,
        modalElement: null,
        toastElement: null
    };

    function getApiUrl(path) {
        if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') {
            return window.ApiClient.getUrl(path.replace(/^\//, ''));
        }
        return path;
    }

    function getAuthHeaders() {
        var headers = {
            'Accept': 'application/json',
            'Content-Type': 'application/json'
        };
        if (window.ApiClient) {
            var token = typeof window.ApiClient.accessToken === 'function'
                ? window.ApiClient.accessToken()
                : window.ApiClient._serverInfo && window.ApiClient._serverInfo.AccessToken;
            if (token) {
                headers['X-Emby-Token'] = token;
            }
        }
        return headers;
    }

    /**
     * Detects the currently playing Jellyfin itemId and mediaSourceId from active playback state,
     * video element src, or URL parameters.
     */
    function detectActivePlaybackContext() {
        var itemId = state.activeItemId;
        var mediaSourceId = state.activeMediaSourceId;

        try {
            // 1. Inspect HTML5 video element src / dataset
            var video = document.querySelector('video.htmlvideoplayer, video');
            if (video) {
                var src = video.currentSrc || video.src || '';
                var videoMatch = src.match(/\/Videos\/([a-f0-9\-]{32,36})\//i);
                if (videoMatch && videoMatch[1]) {
                    itemId = videoMatch[1].replace(/-/g, '');
                }
                var msMatch = src.match(/[?&]MediaSourceId=([a-f0-9\-]{32,36})/i);
                if (msMatch && msMatch[1]) {
                    mediaSourceId = msMatch[1];
                }
            }

            // 2. Inspect URL hash or query parameters
            var href = window.location.href || '';
            if (!itemId) {
                var urlMatch = href.match(/[?&]id=([a-f0-9\-]{32,36})/i);
                if (urlMatch && urlMatch[1]) {
                    itemId = urlMatch[1];
                }
            }

            // 3. Fallback to Emby/Jellyfin PlaybackManager metadata if exposed
            if (!itemId && window.Emby && window.Emby.Page && window.Emby.Page.currentPlayer) {
                var player = window.Emby.Page.currentPlayer;
                if (player._currentPlayOptions && player._currentPlayOptions.item) {
                    itemId = player._currentPlayOptions.item.Id;
                }
            }
        } catch (err) {
            console.warn('[Submarine] Error detecting playback context:', err);
        }

        if (itemId) {
            state.activeItemId = itemId;
        }
        if (mediaSourceId) {
            state.activeMediaSourceId = mediaSourceId;
        }

        return {
            itemId: state.activeItemId,
            mediaSourceId: state.activeMediaSourceId
        };
    }

    function showToast(message) {
        if (!state.toastElement) {
            var toast = document.createElement('div');
            toast.className = 'submarine-toast';
            document.body.appendChild(toast);
            state.toastElement = toast;
        }
        state.toastElement.textContent = message;
        state.toastElement.classList.add('visible');
        clearTimeout(state.toastTimer);
        state.toastTimer = setTimeout(function () {
            if (state.toastElement) {
                state.toastElement.classList.remove('visible');
            }
        }, 3600);
    }

    function ensureModalCreated() {
        if (state.modalElement && document.body.contains(state.modalElement)) {
            return state.modalElement;
        }

        var backdrop = document.createElement('div');
        backdrop.id = 'submarine-modal-backdrop';
        backdrop.setAttribute('role', 'dialog');
        backdrop.setAttribute('aria-modal', 'true');
        backdrop.setAttribute('aria-label', 'Submarine Live Subtitle Search');

        var langOptionsHtml = LANGUAGES.map(function (lang) {
            var selected = lang.code === state.selectedLanguage ? ' selected' : '';
            return '<option value="' + lang.code + '"' + selected + '>' + lang.label + '</option>';
        }).join('');

        var formatPillsHtml = FORMATS.map(function (fmt) {
            var active = fmt === state.selectedFormat ? ' active' : '';
            return '<button type="button" class="submarine-format-pill' + active + '" data-format="' + fmt + '">' +
                fmt.toUpperCase() + '</button>';
        }).join('');

        backdrop.innerHTML =
            '<div class="submarine-modal">' +
                '<div class="submarine-modal-header">' +
                    '<div class="submarine-modal-title-group">' +
                        '<span class="submarine-logo-badge">🌊</span>' +
                        '<div>' +
                            '<h2 class="submarine-modal-title">Submarine Subtitle Search</h2>' +
                            '<p class="submarine-modal-subtitle">Plex-style on-demand subtitles • Live format conversion &amp; instant sync</p>' +
                        '</div>' +
                    '</div>' +
                    '<button type="button" class="submarine-close-btn" title="Close (Esc)" aria-label="Close">&times;</button>' +
                '</div>' +
                '<form class="submarine-search-bar" id="submarine-search-form">' +
                    '<div class="submarine-search-row">' +
                        '<select class="submarine-select" id="submarine-lang-select" aria-label="Subtitle Language">' +
                            langOptionsHtml +
                        '</select>' +
                        '<input type="text" class="submarine-input" id="submarine-query-input" ' +
                            'placeholder="Optional title / release filter (e.g. 1080p BluRay)..." />' +
                        '<button type="submit" class="submarine-btn-primary" id="submarine-search-submit">' +
                            '<span>🔍 Search</span>' +
                        '</button>' +
                    '</div>' +
                    '<div class="submarine-options-row">' +
                        '<div class="submarine-format-pills" id="submarine-format-pills">' +
                            '<span class="submarine-format-label">Target Format:</span>' +
                            formatPillsHtml +
                        '</div>' +
                        '<div class="submarine-sync-controls">' +
                            '<label>Offset (ms): ' +
                                '<input type="number" step="100" min="-60000" max="60000" value="0" ' +
                                    'class="submarine-input submarine-offset-input" id="submarine-offset-input" />' +
                            '</label>' +
                            '<label class="submarine-checkbox-label">' +
                                '<input type="checkbox" id="submarine-persist-checkbox" checked />' +
                                '<span>Save permanently to library</span>' +
                            '</label>' +
                        '</div>' +
                    '</div>' +
                '</form>' +
                '<div class="submarine-results-list" id="submarine-results-list">' +
                    '<div class="submarine-empty-state">Select a language and press Search to find live subtitles.</div>' +
                '</div>' +
            '</div>';

        document.body.appendChild(backdrop);
        state.modalElement = backdrop;

        // Bind events
        var closeBtn = backdrop.querySelector('.submarine-close-btn');
        closeBtn.addEventListener('click', closeModal);

        backdrop.addEventListener('click', function (e) {
            if (e.target === backdrop) {
                closeModal();
            }
        });

        var form = backdrop.querySelector('#submarine-search-form');
        form.addEventListener('submit', function (e) {
            e.preventDefault();
            performSearch();
        });

        var langSelect = backdrop.querySelector('#submarine-lang-select');
        langSelect.addEventListener('change', function () {
            state.selectedLanguage = langSelect.value;
            performSearch();
        });

        var pillsContainer = backdrop.querySelector('#submarine-format-pills');
        pillsContainer.addEventListener('click', function (e) {
            var pill = e.target.closest('.submarine-format-pill');
            if (!pill) {
                return;
            }
            state.selectedFormat = pill.getAttribute('data-format') || 'vtt';
            var allPills = pillsContainer.querySelectorAll('.submarine-format-pill');
            for (var i = 0; i < allPills.length; i++) {
                allPills[i].classList.toggle('active', allPills[i] === pill);
            }
        });

        return backdrop;
    }

    function openModal(customItemId) {
        if (customItemId) {
            state.activeItemId = customItemId;
        } else {
            detectActivePlaybackContext();
        }

        var modal = ensureModalCreated();
        modal.classList.add('submarine-visible');

        performSearch();
    }

    function closeModal() {
        if (state.modalElement) {
            state.modalElement.classList.remove('submarine-visible');
        }
    }

    function escapeHtml(text) {
        var str = String(text == null ? '' : text);
        return str
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function renderResults() {
        if (!state.modalElement) {
            return;
        }
        var listEl = state.modalElement.querySelector('#submarine-results-list');
        if (!listEl) {
            return;
        }

        if (state.loading) {
            listEl.innerHTML = '<div class="submarine-empty-state">⏳ Searching OpenSubtitles, SubDL &amp; configured providers...</div>';
            return;
        }

        if (!state.results || state.results.length === 0) {
            listEl.innerHTML = '<div class="submarine-empty-state">No subtitles found for this query. Try another language or custom filter.</div>';
            return;
        }

        listEl.innerHTML = state.results.map(function (item, idx) {
            var badges = [];
            if (item.ProviderName) {
                badges.push('<span class="submarine-badge submarine-badge-provider">' + escapeHtml(item.ProviderName) + '</span>');
            }
            if (item.IsHashMatch) {
                badges.push('<span class="submarine-badge submarine-badge-hash">★ Hash Match</span>');
            }
            if (item.IsHearingImpaired) {
                badges.push('<span class="submarine-badge submarine-badge-hi">CC / HI</span>');
            }
            var fmt = (item.Format || state.selectedFormat || 'srt').toUpperCase();
            badges.push('<span class="submarine-badge submarine-badge-format">' + escapeHtml(fmt) + ' → ' + state.selectedFormat.toUpperCase() + '</span>');

            if (item.DownloadCount != null) {
                badges.push('<span class="submarine-badge">⬇ ' + Number(item.DownloadCount).toLocaleString() + '</span>');
            }
            if (item.CommunityRating != null && item.CommunityRating > 0) {
                badges.push('<span class="submarine-badge">★ ' + Number(item.CommunityRating).toFixed(1) + '</span>');
            }

            return (
                '<div class="submarine-result-item" tabindex="0" role="button" data-result-index="' + idx + '">' +
                    '<div class="submarine-result-main">' +
                        '<div class="submarine-result-name">' + escapeHtml(item.Name || item.Id) + '</div>' +
                        '<div class="submarine-result-badges">' + badges.join('') + '</div>' +
                    '</div>' +
                    '<span class="submarine-result-action">Activate Live</span>' +
                '</div>'
            );
        }).join('');

        var items = listEl.querySelectorAll('.submarine-result-item');
        for (var i = 0; i < items.length; i++) {
            items[i].addEventListener('click', onResultItemClick);
            items[i].addEventListener('keydown', function (e) {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    onResultItemClick.call(this, e);
                }
            });
        }
    }

    function onResultItemClick() {
        var idx = parseInt(this.getAttribute('data-result-index'), 10);
        var chosen = state.results[idx];
        if (chosen) {
            activateSubtitleResult(chosen);
        }
    }

    function performSearch() {
        detectActivePlaybackContext();
        if (!state.activeItemId) {
            showToast('Submarine: Start playing a video first to search subtitles.');
            return Promise.resolve([]);
        }

        if (state.modalElement) {
            var langSelect = state.modalElement.querySelector('#submarine-lang-select');
            var queryInput = state.modalElement.querySelector('#submarine-query-input');
            if (langSelect) {
                state.selectedLanguage = langSelect.value || 'eng';
            }
            if (queryInput) {
                state.searchQuery = queryInput.value || '';
            }
        }

        state.loading = true;
        renderResults();

        var queryParams =
            'language=' + encodeURIComponent(state.selectedLanguage) +
            '&query=' + encodeURIComponent(state.searchQuery) +
            '&format=' + encodeURIComponent(state.selectedFormat);

        var url = getApiUrl('/Plugins/Submarine/Search/' + encodeURIComponent(state.activeItemId) + '?' + queryParams);

        return fetch(url, {
            method: 'GET',
            headers: getAuthHeaders()
        })
            .then(function (res) {
                if (!res.ok) {
                    throw new Error('HTTP ' + res.status);
                }
                return res.json();
            })
            .then(function (data) {
                state.results = Array.isArray(data) ? data : (data && data.Items ? data.Items : []);
                state.loading = false;
                renderResults();
                return state.results;
            })
            .catch(function (err) {
                console.error('[Submarine] Subtitle search failed:', err);
                state.results = [];
                state.loading = false;
                renderResults();
                return [];
            });
    }

    /**
     * Downloads/converts the chosen subtitle and attaches a live WebVTT <track> directly onto the active
     * HTML5 <video class="htmlvideoplayer"> element so playback continues uninterrupted.
     */
    function activateSubtitleResult(result) {
        detectActivePlaybackContext();
        if (!state.activeItemId || !result || !result.Id) {
            return Promise.resolve(false);
        }

        if (state.modalElement) {
            var offsetEl = state.modalElement.querySelector('#submarine-offset-input');
            var persistEl = state.modalElement.querySelector('#submarine-persist-checkbox');
            state.offsetMs = offsetEl ? (parseInt(offsetEl.value, 10) || 0) : 0;
            state.persistToLibrary = persistEl ? !!persistEl.checked : true;
        }

        var downloadUrl = getApiUrl('/Plugins/Submarine/Download/' + encodeURIComponent(state.activeItemId));
        var payload = {
            SubtitleId: result.Id,
            Language: result.Language || state.selectedLanguage,
            TargetFormat: state.selectedFormat,
            OffsetMs: state.offsetMs,
            PersistToLibrary: state.persistToLibrary
        };

        showToast('🌊 Downloading & converting "' + (result.Name || result.Id) + '" (' + state.selectedFormat.toUpperCase() + ')...');

        return fetch(downloadUrl, {
            method: 'POST',
            headers: getAuthHeaders(),
            body: JSON.stringify(payload)
        })
            .then(function (res) {
                return res.ok ? res.json() : null;
            })
            .catch(function () {
                return null;
            })
            .then(function (downloadResponse) {
                var streamPath = (downloadResponse && downloadResponse.StreamUrl)
                    ? downloadResponse.StreamUrl
                    : '/Plugins/Submarine/StreamById/' +
                      encodeURIComponent(state.activeItemId) + '/' +
                      encodeURIComponent(result.Id) +
                      '/Stream.vtt?offsetMs=' + encodeURIComponent(state.offsetMs);

                var fullTrackUrl = getApiUrl(streamPath);
                attachLiveVideoTrack(fullTrackUrl, result.Language || state.selectedLanguage, result.Name || 'Submarine Live');
                closeModal();
                showToast('✓ Activated live subtitle: ' + (result.Name || result.Id));
                return true;
            });
    }

    function attachLiveVideoTrack(trackUrl, language, label) {
        var video = document.querySelector('video.htmlvideoplayer, video');
        if (!video) {
            return false;
        }

        // Hide existing text tracks
        if (video.textTracks) {
            for (var i = 0; i < video.textTracks.length; i++) {
                video.textTracks[i].mode = 'disabled';
            }
        }

        // Remove any previous Submarine live track elements
        var oldTracks = video.querySelectorAll('track[data-submarine-live="true"]');
        for (var j = 0; j < oldTracks.length; j++) {
            oldTracks[j].remove();
        }

        var track = document.createElement('track');
        track.kind = 'subtitles';
        track.label = '🌊 ' + label;
        track.srclang = (language || 'en').slice(0, 2);
        track.src = trackUrl;
        track.default = true;
        track.setAttribute('data-submarine-live', 'true');
        video.appendChild(track);

        if (track.track) {
            track.track.mode = 'showing';
        }
        setTimeout(function () {
            if (track.track) {
                track.track.mode = 'showing';
            }
        }, 120);

        return true;
    }

    /**
     * Injects Submarine quick-search controls into:
     * 1. The video player OSD bottom bar next to `.btnSubtitles`
     * 2. Any open Jellyfin subtitle actionSheet / popup menu during video playback
     */
    function injectPlayerHooks() {
        var osdSubtitleButtons = document.querySelectorAll('.btnSubtitles');
        for (var i = 0; i < osdSubtitleButtons.length; i++) {
            var subBtn = osdSubtitleButtons[i];
            if (subBtn.parentElement && !subBtn.parentElement.querySelector('.submarine-osd-btn')) {
                var quickBtn = document.createElement('button');
                quickBtn.type = 'button';
                quickBtn.className = 'paper-icon-button-light submarine-osd-btn autoSize';
                quickBtn.title = 'Search & Download Subtitles (Submarine)';
                quickBtn.setAttribute('aria-label', 'Search & Download Subtitles (Submarine)');
                quickBtn.innerHTML = '<span class="material-icons search" aria-hidden="true"></span>';
                quickBtn.addEventListener('click', function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    openModal();
                });
                subBtn.insertAdjacentElement('afterend', quickBtn);
            }
        }

        // Check if a video is currently active on screen
        var activeVideo = document.querySelector('video.htmlvideoplayer, .videoPlayerContainer video, video');
        if (!activeVideo) {
            return;
        }

        var actionScrollers = document.querySelectorAll('.actionSheetScroller');
        for (var k = 0; k < actionScrollers.length; k++) {
            var scroller = actionScrollers[k];
            if (scroller.querySelector('.submarine-menu-action-btn')) {
                continue;
            }

            var actionBtn = document.createElement('button');
            actionBtn.type = 'button';
            actionBtn.className = 'listItem listItem-button actionSheetMenuItem emby-button submarine-menu-action-btn';
            actionBtn.innerHTML = '<span>🔍 Search &amp; Download Subtitles (Submarine)...</span>';
            actionBtn.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                openModal();
            });
            scroller.insertBefore(actionBtn, scroller.firstChild);
        }
    }

    function startObserver() {
        if (!document.body) {
            window.addEventListener('DOMContentLoaded', startObserver);
            return;
        }

        injectPlayerHooks();

        var observer = new MutationObserver(function () {
            injectPlayerHooks();
        });

        observer.observe(document.body, {
            childList: true,
            subtree: true
        });

        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && state.modalElement && state.modalElement.classList.contains('submarine-visible')) {
                closeModal();
            }
        });
    }

    window.SubmarinePlayer = {
        __initialized: true,
        openModal: openModal,
        closeModal: closeModal,
        performSearch: performSearch,
        activateSubtitleResult: activateSubtitleResult,
        attachLiveVideoTrack: attachLiveVideoTrack,
        detectActivePlaybackContext: detectActivePlaybackContext
    };

    startObserver();
})();
