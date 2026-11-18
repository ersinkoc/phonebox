<p align="center"><img src="../agent/PhoneBox.Agent/phonebox.ico" width="72" alt="Icono de PhoneBox"></p>

<h1 align="center">PhoneBox</h1>

<p align="center">
  <a href="../README.md">English</a> ·
  <a href="README.tr.md">Türkçe</a> ·
  <a href="README.de.md">Deutsch</a> ·
  <a href="README.fr.md">Français</a> ·
  <b>Español</b> ·
  <a href="README.it.md">Italiano</a> ·
  <a href="README.pt.md">Português</a> ·
  <a href="README.nl.md">Nederlands</a> ·
  <a href="README.pl.md">Polski</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.vi.md">Tiếng Việt</a> ·
  <a href="README.zh.md">中文 (简体)</a>
</p>

<p align="center"><b>Convierte un teléfono Android antiguo en un panel de escritorio para tu ordenador.</b><br>
Estadísticas del PC, sesiones de programación con IA, música y controles táctiles por USB o Wi-Fi.<br>
Windows, Linux y macOS. Sin root, sin cuenta de PhoneBox ni servicio en la nube.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Probar demo en vivo</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Descargar</b></a> ·
  <a href="#inicio-rápido"><b>Inicio rápido</b></a>
</p>

<p align="center">
  <a href="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml"><img src="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml/badge.svg" alt="Estado de compilación"></a>
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><img src="https://img.shields.io/github/v/release/ersinkoc/phonebox?label=release" alt="Última versión"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-5%2B-3ddc84" alt="Android 5+">
  <a href="../LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="Licencia MIT"></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox en tres teléfonos"><br>
<sub>Diseños de ejemplo con datos de demostración.</sub></p>

PhoneBox combina una **aplicación Android** a pantalla completa con un ligero **agente de escritorio**. Diseña tu panel en un editor visual web: **123 widgets**, **30 plantillas**, varias páginas y vista previa en vivo.

## Qué puedes hacer

| Uso | Qué aparece en el teléfono |
|---|---|
| **Monitorización del PC** | CPU, GPU, RAM, discos, red y ping; gráficos, detalles de GPU y control de procesos |
| **Programación con IA** | Cuotas de Claude, Codex y Antigravity; sesiones locales de Claude Code y Codex; revisiones de GitHub y estado CI |
| **Controles táctiles** | Atajos de apps y URL, macros; audio, micrófono, multimedia, OBS y sistema mediante [Control Deck](CONTROL-DECK.md) |
| **Uso diario** | Reproducción actual, radio por internet, relojes, clima, calendarios, notas, listas, pomodoro y temporizadores |
| **Automatización** | Webhooks, notificaciones, Home Assistant y reglas reactivas |

## Inicio rápido

### 1. Instalar el agente de escritorio

**Windows 10/11 x64** (PowerShell):

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

**Linux o macOS**:

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

La configuración se abre en <http://127.0.0.1:8765>. Consulta la [guía de instalación](SETUP.md).

### 2. Conectar el teléfono

| USB | Solo Wi-Fi |
|---|---|
| Activa **Opciones de desarrollador → Depuración USB** y conecta el cable. | Instala el APK de la [última versión](https://github.com/ersinkoc/phonebox/releases/latest), activa **Wi-Fi & Tailscale** y escanea el código QR. |

### 3. Personalizar

Abre <http://127.0.0.1:8765>, elige una plantilla en **Páginas**, organiza tus widgets en la cuadrícula 12×6 y pulsa **Guardar**.

## Documentación

| Guía | Contenido |
|---|---|
| [Instalación y conexiones](SETUP.md) | Opciones, USB, Wi-Fi, Tailscale, actualizaciones y solución de problemas |
| [Guía de usuario](GUIDE.md) | Plantillas, widgets, integraciones IA, editor, webhooks y reglas |
| [Apariencia](APPEARANCE.md) | Fuentes, superficies, preajustes y vista previa |
| [Control Deck](CONTROL-DECK.md) | Audio, multimedia, OBS, atajos y tareas |
| [Desarrollo](DEVELOPMENT.md) | Arquitectura, demo local, compilación y pruebas |
| [Privacidad](../PRIVACY.md) | Conexiones salientes y seguridad |

## Licencia

[MIT](../LICENSE)
