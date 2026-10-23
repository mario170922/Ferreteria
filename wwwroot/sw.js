// ==========================================================
// sw.js — "trabajador" que permite instalar el sitio en el celular como una app.
//
// - Las páginas (inventario, pedidos...) SIEMPRE se piden al servidor, para que
//   los datos estén al día. No se guardan copias de páginas con datos.
// - Los archivos de diseño (Bootstrap, íconos, letras, estilos, logo) se guardan
//   en el celular para que la app abra más rápido.
// - Si no hay conexión con el servidor, se muestra la página "Sin conexión".
//
// Si se cambia algo de este archivo, subir el número de VERSION.
// ==========================================================
const VERSION = 'castillo-v1';

const BASICOS = [
    '/sin-conexion.html',
    '/lib/bootstrap-5.3.3/css/bootstrap.min.css',
    '/lib/bootstrap-icons/bootstrap-icons.min.css',
    '/lib/bootstrap-icons/fonts/bootstrap-icons.woff2',
    '/lib/barlow/barlow.css',
    '/img/logo-casita.png',
    '/img/patron-tornillos.svg'
];

self.addEventListener('install', function (ev) {
    ev.waitUntil(caches.open(VERSION).then(function (c) { return c.addAll(BASICOS); }));
    self.skipWaiting();
});

self.addEventListener('activate', function (ev) {
    // Se borran las copias de versiones anteriores
    ev.waitUntil(caches.keys().then(function (nombres) {
        return Promise.all(nombres.filter(function (n) { return n !== VERSION; })
            .map(function (n) { return caches.delete(n); }));
    }).then(function () { return self.clients.claim(); }));
});

function esArchivoDeDiseno(url) {
    return /^\/(lib|css|js|img\/app)\//.test(url.pathname) ||
        url.pathname === '/img/logo-casita.png' ||
        url.pathname === '/img/patron-tornillos.svg';
}

self.addEventListener('fetch', function (ev) {
    const pedido = ev.request;
    if (pedido.method !== 'GET') return;

    const url = new URL(pedido.url);
    if (url.origin !== self.location.origin) return;

    // Páginas: siempre del servidor; si no responde, la página "Sin conexión".
    if (pedido.mode === 'navigate') {
        ev.respondWith(fetch(pedido).catch(function () { return caches.match('/sin-conexion.html'); }));
        return;
    }

    // Diseño: se entrega la copia guardada y por detrás se actualiza.
    if (esArchivoDeDiseno(url)) {
        ev.respondWith(caches.open(VERSION).then(function (c) {
            return c.match(pedido).then(function (guardado) {
                const nuevo = fetch(pedido).then(function (resp) {
                    if (resp.ok) c.put(pedido, resp.clone());
                    return resp;
                }).catch(function () { return guardado; });
                return guardado || nuevo;
            });
        }));
    }
});
