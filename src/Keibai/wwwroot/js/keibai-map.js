// Keibai property map — plain JS, no build step. Renders a pannable Leaflet map of the search
// results (all pins matching the current /jp filters, fetched from the div's data-pins-url).
// Every pin is an individual marker — no proximity clustering. The one exception: pins sharing the
// EXACT same coordinate (BIT occasionally geocodes related parcels to one point; stacks of 2–3 in
// the current corpus) are grouped into a single numbered marker whose popup lists every property at
// that point — otherwise only the top marker of the stack would ever be clickable.
//
// The page is static SSR with Blazor enhanced navigation (blazor.web.js swaps the DOM without a full
// reload), so we (re)initialise both on DOMContentLoaded and on Blazor's 'enhancedload' event, and
// tear down any previous map first (module-level handle).

(function () {
    "use strict";

    // Module-level handle so a re-init after enhanced navigation can dispose the old map cleanly.
    var mapInstance = null;

    var JAPAN_CENTER = [36.5, 138.0];
    var JAPAN_ZOOM = 5;

    // Escape untrusted server strings (addresses etc.) before interpolating into popup HTML.
    function esc(value) {
        if (value === null || value === undefined) {
            return "";
        }
        return String(value)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#39;");
    }

    // ¥1,234,000 with Japanese grouping, or — when the amount is null/undefined.
    function yen(amount) {
        if (amount === null || amount === undefined) {
            return "—";
        }
        return "¥" + Number(amount).toLocaleString("ja-JP");
    }

    function orDash(value) {
        return (value === null || value === undefined || value === "") ? "—" : esc(value);
    }

    function popupHtml(pin) {
        var detailUrl = "/jp/property/" + encodeURIComponent(pin.courtId) + "/" + encodeURIComponent(pin.saleUnitId);
        return (
            '<div class="keibai-map-popup">' +
            '<div class="mb-1">' +
            '<span class="badge text-bg-secondary me-1">' + orDash(pin.typeLabel) + "</span>" +
            '<span class="badge text-bg-info">' + orDash(pin.statusLabel) + "</span>" +
            "</div>" +
            '<div class="mb-1">' + orDash(pin.address) + "</div>" +
            '<div class="mb-1 jp-num">売却基準価額 ' + esc(yen(pin.price)) +
            " / 買受可能価額 " + esc(yen(pin.minBid)) + "</div>" +
            '<div class="mb-2 jp-num">入札締切 ' + orDash(pin.biddingEnd) + "</div>" +
            '<a href="' + detailUrl + '" target="_blank" rel="noopener" class="fw-semibold">詳細を見る →</a>' +
            "</div>"
        );
    }

    function setCount(text) {
        var el = document.getElementById("prop-map-count");
        if (el) {
            el.textContent = text;
        }
    }

    function destroyMap() {
        if (mapInstance) {
            try {
                mapInstance.remove();
            } catch (e) {
                // ignore — map may already be gone if the DOM was swapped out.
            }
            mapInstance = null;
        }
    }

    function initMap() {
        var el = document.getElementById("prop-map");
        if (!el) {
            return; // Only the /jp search page has this element.
        }
        if (typeof L === "undefined") {
            return; // Leaflet failed to load; leave the map area empty rather than crashing.
        }

        // Dispose any prior instance (enhanced navigation may re-run this without a full reload).
        destroyMap();

        // Self-hosted marker images live alongside leaflet.css under /lib/leaflet/images.
        L.Icon.Default.imagePath = "/lib/leaflet/images/";

        var map = L.map(el).setView(JAPAN_CENTER, JAPAN_ZOOM);
        mapInstance = map;

        // GSI 淡色地図 (pale) base tiles.
        L.tileLayer("https://cyberjapandata.gsi.go.jp/xyz/pale/{z}/{x}/{y}.png", {
            maxZoom: 18,
            attribution:
                '<a href="https://maps.gsi.go.jp/development/ichiran.html">国土地理院</a>'
        }).addTo(map);

        var pinsUrl = el.getAttribute("data-pins-url");
        if (!pinsUrl) {
            setCount("地図データを読み込めませんでした / Could not load map data.");
            return;
        }

        fetch(pinsUrl, { headers: { Accept: "application/json" } })
            .then(function (resp) {
                if (!resp.ok) {
                    throw new Error("HTTP " + resp.status);
                }
                return resp.json();
            })
            .then(function (data) {
                renderPins(map, data);
            })
            .catch(function () {
                setCount("地図データを読み込めませんでした / Could not load map data.");
            });
    }

    // Popup for a same-coordinate stack: a header plus one compact row per property. Scrolls via the
    // popup's maxHeight when the stack is deep.
    function stackPopupHtml(group) {
        var rows = "";
        for (var i = 0; i < group.length; i++) {
            var pin = group[i];
            var detailUrl = "/jp/property/" + encodeURIComponent(pin.courtId) + "/" + encodeURIComponent(pin.saleUnitId);
            rows +=
                '<div class="border-top py-1">' +
                '<span class="badge text-bg-secondary me-1">' + orDash(pin.typeLabel) + "</span>" +
                '<span class="jp-num me-1">' + esc(yen(pin.price)) + "</span>" +
                '<a href="' + detailUrl + '" target="_blank" rel="noopener" class="fw-semibold">詳細 →</a>' +
                '<div class="small text-muted">' + orDash(pin.address) + "</div>" +
                "</div>";
        }
        return (
            '<div class="keibai-map-popup">' +
            '<div class="fw-semibold mb-1">この地点に ' + group.length + " 件 / " + group.length + " properties</div>" +
            rows +
            "</div>"
        );
    }

    function renderPins(map, data) {
        var pins = (data && data.pins) || [];

        // Group by exact coordinate so stacked pins stay clickable (see file header).
        var byCoord = {};
        for (var i = 0; i < pins.length; i++) {
            var pin = pins[i];
            if (typeof pin.lat !== "number" || typeof pin.lng !== "number") {
                continue;
            }
            var key = pin.lat + "," + pin.lng;
            (byCoord[key] || (byCoord[key] = [])).push(pin);
        }

        var markers = [];
        for (var key2 in byCoord) {
            var group = byCoord[key2];
            var latlng = [group[0].lat, group[0].lng];
            var marker;
            if (group.length === 1) {
                marker = L.marker(latlng);
                marker.bindPopup(popupHtml(group[0]));
            } else {
                marker = L.marker(latlng, {
                    icon: L.divIcon({
                        className: "keibai-pin-stack",
                        html: "<span>" + group.length + "</span>",
                        iconSize: [30, 30],
                        iconAnchor: [15, 15]
                    })
                });
                marker.bindPopup(stackPopupHtml(group), { maxHeight: 260, minWidth: 240 });
            }
            markers.push(marker);
        }

        L.layerGroup(markers).addTo(map);

        // Fit to the pins, or fall back to a whole-Japan view when there are none.
        if (markers.length > 0) {
            var group = L.featureGroup(markers);
            map.fitBounds(group.getBounds(), { padding: [30, 30] });
        } else {
            map.setView(JAPAN_CENTER, JAPAN_ZOOM);
        }

        // Footer count: "123件表示中" (+ 位置情報なし / 上限で切り捨て annotations).
        var total = typeof data.total === "number" ? data.total : markers.length;
        var text = total.toLocaleString("ja-JP") + "件表示中";
        if (data.withoutCoords > 0) {
            text += " / 位置情報なし " + Number(data.withoutCoords).toLocaleString("ja-JP") + "件";
        }
        if (data.capped) {
            text += "（上限で切り捨て）";
        }
        setCount(text);
    }

    // Initial (non-enhanced) page load.
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initMap);
    } else {
        initMap();
    }

    // Blazor enhanced navigation swaps the DOM in place; re-init on each enhanced load. This script
    // tag sits after blazor.web.js, but guard anyway in case the Blazor global isn't ready yet.
    if (typeof Blazor !== "undefined" && Blazor.addEventListener) {
        Blazor.addEventListener("enhancedload", initMap);
    }
})();
