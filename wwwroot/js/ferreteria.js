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
        registrarApp();
        lectorDeBarras();
    });

    // El lector de código de barras escribe el código y presiona Enter solo.
    // En los campos .js-codigo-barras ese Enter no envía el formulario: pasa al siguiente campo.
    function lectorDeBarras() {
        document.querySelectorAll('.js-codigo-barras').forEach(function (campo) {
            campo.addEventListener('keydown', function (ev) {
                if (ev.key !== 'Enter') return;
                ev.preventDefault();
                var campos = Array.prototype.slice.call(campo.form.querySelectorAll('input:not([type=hidden]), select, textarea'));
                var siguiente = campos[campos.indexOf(campo) + 1];
                if (siguiente) siguiente.focus();
            });
        });
    }

    // Permite instalar el sitio en el celular como una app (ver /sw.js).
    // El navegador solo lo acepta en https o en localhost; en otros casos no pasa nada.
    function registrarApp() {
        if (!('serviceWorker' in navigator) || !window.isSecureContext) return;
        navigator.serviceWorker.register('/sw.js').catch(function () { });
    }

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

    // ---------- Ventanas de aviso y confirmación con los colores de la marca ----------
    // Reemplazan las ventanas grises del navegador (alert y confirm).
    //
    // Para pedir confirmación antes de enviar un formulario, se pone en el <form>,
    // en su botón o en una <option> elegida dentro del formulario:
    //   data-confirmar="¿Eliminar este producto?"
    //   data-confirmar-boton="Sí, eliminar"      (texto del botón de aceptar, opcional)
    //   data-confirmar-tipo="peligro"            (botón rojo; si no se pone, amarillo)
    //
    // Para un aviso simple desde JavaScript: ferAviso('Texto del aviso');

    var ventana = null;

    function crearVentana() {
        if (ventana) return ventana;
        var div = document.createElement('div');
        div.className = 'modal fade fer-dialogo';
        div.tabIndex = -1;
        div.setAttribute('aria-modal', 'true');
        div.setAttribute('role', 'dialog');
        div.setAttribute('aria-labelledby', 'fer-dialogo-titulo');
        div.innerHTML =
            '<div class="modal-dialog modal-dialog-centered">' +
            '  <div class="modal-content">' +
            '    <div class="modal-body text-center">' +
            '      <span class="fer-dialogo-icono" aria-hidden="true"><i class="bi"></i></span>' +
            '      <h2 id="fer-dialogo-titulo" class="fer-dialogo-titulo"></h2>' +
            '      <p class="fer-dialogo-texto mb-0"></p>' +
            '    </div>' +
            '    <div class="modal-footer">' +
            '      <button type="button" class="btn btn-fer-outline fer-dialogo-no" data-bs-dismiss="modal"></button>' +
            '      <button type="button" class="btn fer-dialogo-si"></button>' +
            '    </div>' +
            '  </div>' +
            '</div>';
        document.body.appendChild(div);
        ventana = div;
        return div;
    }

    // Muestra la ventana. alAceptar se llama solo si la persona presiona el botón principal.
    function mostrarVentana(opciones, alAceptar) {
        // Si Bootstrap no cargó, se usa la ventana normal del navegador para no bloquear nada.
        if (!window.bootstrap || !window.bootstrap.Modal) {
            if (opciones.soloAviso) { window.alert(opciones.texto); return; }
            if (window.confirm(opciones.texto) && alAceptar) alAceptar();
            return;
        }

        var div = crearVentana();
        var peligro = opciones.tipo === 'peligro';
        div.classList.toggle('fer-dialogo-peligro', peligro);
        div.querySelector('.fer-dialogo-icono .bi').className =
            'bi ' + (opciones.soloAviso ? 'bi-info-circle' : (peligro ? 'bi-exclamation-octagon' : 'bi-question-circle'));
        div.querySelector('.fer-dialogo-titulo').textContent = opciones.titulo || (opciones.soloAviso ? 'Aviso' : '¿Está seguro?');
        div.querySelector('.fer-dialogo-texto').textContent = opciones.texto;

        var no = div.querySelector('.fer-dialogo-no');
        var si = div.querySelector('.fer-dialogo-si');
        no.textContent = 'No, volver';
        no.hidden = !!opciones.soloAviso;
        si.textContent = opciones.boton || (opciones.soloAviso ? 'Entendido' : 'Sí, continuar');
        si.className = 'btn fer-dialogo-si ' + (peligro ? 'btn-fer-peligro' : 'btn-fer');

        var modal = window.bootstrap.Modal.getOrCreateInstance(div);
        var aceptado = false;
        si.onclick = function () { aceptado = true; modal.hide(); };
        div.addEventListener('hidden.bs.modal', function alCerrar() {
            div.removeEventListener('hidden.bs.modal', alCerrar);
            if (aceptado && alAceptar) alAceptar();
        });
        div.addEventListener('shown.bs.modal', function alAbrir() {
            div.removeEventListener('shown.bs.modal', alAbrir);
            // En confirmaciones peligrosas el foco queda en "No", para que un Enter no borre nada.
            (opciones.soloAviso || !peligro ? si : no).focus();
        });
        modal.show();
    }

    window.ferAviso = function (texto, titulo) {
        mostrarVentana({ texto: texto, titulo: titulo, soloAviso: true });
    };

    // Busca el mensaje de confirmación: primero en el botón, luego en la opción elegida y al final en el formulario.
    function datosConfirmacion(form, boton) {
        var fuentes = [boton, form.querySelector('option[data-confirmar]:checked'), form];
        for (var i = 0; i < fuentes.length; i++) {
            var el = fuentes[i];
            if (el && el.dataset && el.dataset.confirmar) {
                return { texto: el.dataset.confirmar, boton: el.dataset.confirmarBoton, tipo: el.dataset.confirmarTipo, titulo: el.dataset.confirmarTitulo };
            }
        }
        return null;
    }

    // Se escucha en la fase de captura, antes que cualquier otro código del formulario.
    document.addEventListener('submit', function (ev) {
        var form = ev.target;
        if (!(form instanceof HTMLFormElement)) return;
        if (form.dataset.confirmado === '1') { delete form.dataset.confirmado; return; }

        var boton = ev.submitter || null;
        var datos = datosConfirmacion(form, boton);
        if (!datos) return;

        ev.preventDefault();
        ev.stopImmediatePropagation();
        mostrarVentana(datos, function () {
            form.dataset.confirmado = '1';
            if (form.requestSubmit) form.requestSubmit(boton && boton.form === form ? boton : undefined);
            else form.submit();
        });
    }, true);
})();
