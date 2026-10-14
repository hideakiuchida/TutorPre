import { Component, computed, inject } from '@angular/core';
import { EntradaPage } from './acceso/entrada.page';
import { ProfesorPage } from './acceso/profesor.page';
import { AdminPage } from './admin/admin.page';
import { ChatTutorPage } from './chat/chat-tutor.page';
import { SesionService } from './datos/sesion.service';
import { DocentePage } from './docente/docente.page';

/**
 * Sin sesión, la página de entrada (RF-31); con sesión, la pantalla de su rol. El retorno
 * del proveedor de identidad (/entrar/…) y el enlace para restablecer la contraseña
 * (/restablecer) siempre los atiende la entrada. El alumno sin
 * profesor, o que quiere cambiarlo, pasa antes por la elección de profesor.
 */
@Component({
  selector: 'app-root',
  imports: [EntradaPage, ChatTutorPage, DocentePage, AdminPage, ProfesorPage],
  template: `
    @switch (vista()) {
      @case ('entrada') { <app-entrada /> }
      @case ('profesor') { <app-profesor /> }
      @case ('Alumno') { <app-chat-tutor /> }
      @case ('Docente') { <app-docente /> }
      @case ('Admin') { <app-admin /> }
    }
  `,
})
export class App {
  private readonly sesion = inject(SesionService);

  readonly vista = computed(() => {
    const usuario = this.sesion.usuario();
    const enEntrada = location.pathname.startsWith('/entrar/') || location.pathname.startsWith('/restablecer');
    if (!usuario || enEntrada) return 'entrada';
    return usuario.rol === 'Alumno' && this.sesion.eligiendoProfesor() ? 'profesor' : usuario.rol;
  });
}
