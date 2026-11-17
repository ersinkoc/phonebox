<p align="center"><img src="../agent/PhoneBox.Agent/phonebox.ico" width="72" alt="Ícone do PhoneBox"></p>

<h1 align="center">PhoneBox</h1>

<p align="center">
  <a href="../README.md">English</a> ·
  <a href="README.tr.md">Türkçe</a> ·
  <a href="README.de.md">Deutsch</a> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.it.md">Italiano</a> ·
  <b>Português</b> ·
  <a href="README.nl.md">Nederlands</a> ·
  <a href="README.pl.md">Polski</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.vi.md">Tiếng Việt</a> ·
  <a href="README.zh.md">中文 (简体)</a>
</p>

<p align="center"><b>Transforme um telemóvel Android antigo num painel de secretária para o seu computador.</b><br>
Estatísticas do PC, sessões de programação com IA, música e controlos táteis por USB ou Wi-Fi.<br>
Windows, Linux e macOS. Sem root, sem conta PhoneBox e sem serviço na nuvem.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Experimentar demonstração</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Transferir</b></a> ·
  <a href="#início-rápido"><b>Início rápido</b></a>
</p>

<p align="center">
  <a href="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml"><img src="https://github.com/ersinkoc/phonebox/actions/workflows/build.yml/badge.svg" alt="Estado da compilação"></a>
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><img src="https://img.shields.io/github/v/release/ersinkoc/phonebox?label=release" alt="Última versão"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-7%2B-3ddc84" alt="Android 7+">
  <a href="../LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="Licença MIT"></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox em três telemóveis"><br>
<sub>Esquemas de exemplo com dados de demonstração.</sub></p>

O PhoneBox combina uma **aplicação Android** em ecrã inteiro com um pequeno **agente de ambiente de trabalho**. Crie o seu painel no editor visual no navegador: **122 widgets**, **30 modelos**, várias páginas e pré-visualização ao vivo.

## O que pode fazer

| Utilização | O que aparece no telemóvel |
|---|---|
| **Monitorização do PC** | CPU, GPU, RAM, discos, rede e ping; gráficos, detalhes da GPU e controlo de processos |
| **Programação com IA** | Quotas de Claude, Codex e Antigravity; sessões locais de Claude Code e Codex; revisões GitHub e estado CI |
| **Controlos táteis** | Atalhos de apps e URL, macros; áudio, microfone, multimédia, OBS e sistema via [Control Deck](CONTROL-DECK.md) |
| **Uso diário** | A reproduzir, rádio na internet, relógios, meteorologia, calendários, notas, listas, pomodoro e temporizadores |
| **Automação** | Webhooks, notificações, Home Assistant e regras reativas |

## Início rápido

### 1. Instalar o agente de ambiente de trabalho

**Windows 10/11 x64** (PowerShell):

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

**Linux ou macOS**:

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

As definições abrem em <http://127.0.0.1:8765>. Consulte o [guia de instalação](SETUP.md).

### 2. Ligar o telemóvel

| USB | Apenas Wi-Fi |
|---|---|
| Ative **Opções de programador → Depuração USB** e ligue o cabo. | Instale o APK da [última versão](https://github.com/ersinkoc/phonebox/releases/latest), ative **Wi-Fi & Tailscale** e leia o código QR. |

### 3. Personalizar

Abra <http://127.0.0.1:8765>, escolha um modelo em **Páginas**, organize os widgets na grelha 12×6 e clique em **Guardar**.

## Documentação

| Guia | Conteúdo |
|---|---|
| [Instalação e ligações](SETUP.md) | Opções, USB, Wi-Fi, Tailscale, atualizações e resolução de problemas |
| [Guia do utilizador](GUIDE.md) | Modelos, widgets, integrações de IA, editor, webhooks e regras |
| [Aparência](APPEARANCE.md) | Tipos de letra, superfícies, predefinições e pré-visualização |
| [Control Deck](CONTROL-DECK.md) | Áudio, multimédia, OBS, atalhos e tarefas |
| [Desenvolvimento](DEVELOPMENT.md) | Arquitetura, demonstração local, compilação e testes |
| [Privacidade](../PRIVACY.md) | Ligações de saída e segurança |

## Licença

[MIT](../LICENSE)
