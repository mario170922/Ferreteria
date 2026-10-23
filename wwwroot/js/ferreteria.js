// ==========================================================
// ferreteria.js
// Detalles de movimiento para todo el sitio. Si algo falla aquí,
// la página sigue funcionando igual: solo se pierden las animaciones.
// ==========================================================
(function () {
    'use strict';

    var sinMovimiento = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    function alCargar(fn) {
        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', fn);
        else fn();
    }

    alCargar(function () {
        marcarPaginaActual();
        sombraEnLaBarra();
        revelarAlBajar();
        botonesQueTrabajan();
        cerrarMensajesDeExito();
        botonVolverArriba();
        transicionEntrePaginas();
    });

    // La página donde está el usuario queda resaltada en la barra superior.
    function marcarPaginaActual() {
        var ruta = location.pathname.replace(/\/$/, '').toLowerCase() || '/';
        var mejor = null, largo = -1;

        document.querySelectorAll('.fer-nav .btn-light, .ec-nav .btn-light').forEach(function (a) {
            var destino = (a.getAttribute('href') || '').replace(/\/$/, '').toLowerCase() || '/';
            var coincide = destino === '/' ? ruta === '/' : (ruta === destino || ruta.indexOf(destino + '/') === 0);
            // "Pedidos" cubre todas las páginas de pedidos
            if (!coincide && destino === '/pedidos/lista' && ruta.indexOf('/pedidos') === 0) coincide = true;
            if (!coincide && destino === '/productos' && /^\/(movimientos|editar|nuevo|ajustar)/.test(ruta)) coincide = true;
            if (coincide && destino.length > largo) { mejor = a; largo = destino.length; }
        });

        if (mejor) {
            mejor.classList.add('activo');
            mejor.setAttribute('aria-current', 'page');
        }
    }

    // La barra superior toma sombra cuando se baja en la página.
    function sombraEnLaBarra() {
        var barra = document.querySelector('.fer-nav, .ec-nav');
        if (!barra) return;
        var revisar = function () { barra.classList.toggle('con-sombra', window.scrollY > 8); };
        window.addEventListener('scroll', revisar, { passive: true });
        revisar();
    }

    // Tarjetas, secciones y filas suben suavemente al aparecer en pantalla.
    function revelarAlBajar() {
        if (sinMovimiento || !('IntersectionObserver' in window)) return;

        var elementos = document.querySelectorAll(
            'main .fer-card, main .ec-tarjeta, main .producto-card, main .fer-portada, main .fer-tabla tbody tr, main .al-linea');

        // Si hay demasiados (por ejemplo una tabla larga), solo se animan los primeros.
        var limite = 60;

        var observador = new IntersectionObserver(function (entradas) {
            entradas.forEach(function (e) {
                if (e.isIntersecting) {
                    e.target.classList.add('visible');
                    observador.unobserve(e.target);
                }
            });
        }, { rootMargin: '0px 0px -30px 0px' });

        var porPadre = new Map();
        elementos.forEach(function (el, i) {
            if (i >= limite) return;
            // Pequeño escalonado entre hermanos para que no aparezcan todos de golpe
            var padre = el.parentElement;
            var n = porPadre.get(padre) || 0;
            porPadre.set(padre, n + 1);
            el.style.setProperty('--fer-retraso', Math.min(n * 40, 240) + 'ms');
            el.classList.add('fer-revelar');
            observador.observe(el);
        });
    }

    // Al enviar un formulario, su botón muestra "Guardando..." y no se puede tocar dos veces.
    function botonesQueTrabajan() {
        document.querySelectorAll('form').forEach(function (form) {
            form.addEventListener('submit', function (ev) {
                if (ev.defaultPrevented) return;
                if ((form.getAttribute('method') || 'get').toLowerCase() !== 'post') return;
                var boton = ev.submitter || form.querySelector('button[type="submit"]');
                if (!boton || boton.classList.contains('enviando')) return;

                // Se deja un instante para que el navegador envíe el valor del botón
                setTimeout(function () {
                    boton.classList.add('enviando');
                    boton.setAttribute('aria-busy', 'true');
                    var giro = document.createElement('span');
                    giro.className = 'fer-girando';
                    giro.setAttribute('aria-hidden', 'true');
                    boton.insertBefore(giro, boton.firstChild);
                }, 0);
            });
        });
    }

    // Los mensajes verdes de "listo" se cierran solos después de unos segundos.
    function cerrarMensajesDeExito() {
        document.querySelectorAll('main .alert-success').forEach(function (alerta) {
            setTimeout(function () {
                alerta.classList.add('saliendo');
                setTimeout(function () { alerta.remove(); }, 260);
            }, 6000);
        });
    }

    // Botón redondo para volver al inicio de la página en listas largas.
    function botonVolverArriba() {
        var boton = document.createElement('button');
        boton.type = 'button';
        boton.className = 'fer-arriba no-imprimir';
        boton.setAttribute('aria-label', 'Volver arriba');
        boton.innerHTML = '<i class="bi bi-arrow-up" aria-hidden="true"></i>';
        boton.addEventListener('click', function () { window.scrollTo({ top: 0, behavior: sinMovimiento ? 'auto' : 'smooth' }); });
        document.body.appendChild(boton);

        var revisar = function () { boton.classList.toggle('visible', window.scrollY > 600); };
        window.addEventListener('scroll', revisar, { passive: true });
        revisar();
    }

    // Al ir a otra página del sitio, la actual se desvanece un instante.
    function transicionEntrePaginas() {
        if (sinMovimiento) return;

        document.addEventListener('click', function (ev) {
            var a = ev.target.closest && ev.target.closest('a[href]');
            if (!a || ev.defaultPrevented || ev.button !== 0 || ev.ctrlKey || ev.metaKey || ev.shiftKey || ev.altKey) return;
            if (a.target && a.target !== '_self') return;
            if (a.hasAttribute('download')) return;

            var href = a.getAttribute('href');
            if (!href || href.charAt(0) === '#' || /^(tel|mailto|javascript):/i.test(href)) return;
            if (a.origin !== location.origin) return;

            ev.preventDefault();
            document.body.classList.add('fer-saliendo');
            setTimeout(function () { location.href = a.href; }, 110);
        });

        // Al volver con el botón "atrás" del navegador la página debe verse normal
        window.addEventListener('pageshow', function () { document.body.classList.remove('fer-saliendo'); });
    }
})();
