<p align="center"><img src="../agent/PhoneBox.Agent/phonebox.ico" width="72" alt="PhoneBox"></p>

<h1 align="center">PhoneBox</h1>

<p align="center">
  <a href="../README.md">English</a> ·
  <a href="README.tr.md">Türkçe</a> ·
  <a href="README.de.md">Deutsch</a> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.it.md">Italiano</a> ·
  <a href="README.pt.md">Português</a> ·
  <b>Português (Brasil)</b> ·
  <a href="README.nl.md">Nederlands</a> ·
  <a href="README.pl.md">Polski</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.el.md">Ελληνικά</a> ·
  <a href="README.vi.md">Tiếng Việt</a> ·
  <a href="README.zh.md">中文 (简体)</a> ·
  <a href="README.ja.md">日本語</a> ·
  <a href="README.ko.md">한국어</a> ·
  <a href="README.id.md">Bahasa Indonesia</a> ·
  <a href="README.th.md">ไทย</a> ·
  <a href="README.uk.md">Українська</a> ·
  <a href="README.hi.md">हिन्दी</a> ·
  <a href="README.cs.md">Čeština</a> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.zh-Hant.md">中文（繁體）</a>
</p>

<p align="center"><b>Transforme um celular Android antigo em um painel de mesa para o seu computador.</b><br>
Estatísticas do PC, sessões de programação com IA, música e controles por toque via USB ou Wi-Fi.</p>

<p align="center">
  <a href="https://ersinkoc.github.io/phonebox/"><b>Experimente a demonstração ao vivo</b></a> ·
  <a href="https://github.com/ersinkoc/phonebox/releases/latest"><b>Baixar</b></a>
</p>

<p align="center"><img src="hero-0.6.png" width="900" alt="PhoneBox mostrando as páginas de automação, home e planner em três telefones Android"></p>

## O que você pode fazer

- Monitore CPU, GPU, memória, discos, rede e processos em execução.
- Acompanhe sessões do Claude e do Codex, cotas de IA, revisões do GitHub e o status da CI.
- Execute atalhos e macros, ajuste o áudio e controle o OBS com o Control Deck.
- Mostrar relógios, tempo, calendários, notas, mídia e controles Home Assistant.
- Monte páginas e widgets no editor visual e visualize as mudanças antes de salvar.

<p align="center"><a href="tour-0.6.mp4"><img src="tour-0.6.gif" width="780" alt="PhoneBox tour mostrando notificações, solicitações de permissão e troca automática de página"></a><br>
<sub>Dados de demonstração: notificações, solicitações de permissão e troca automática de página. <a href="tour-0.6.mp4">Assista ao MP4</a></sub></p>

## Quick start

Use um telefone Android 5.0 ou posterior (recomenda-se o Android 6 ou posterior) e um computador com suporte.O agente inclui o tempo de execução do .NET.

### 1. Instalar o agente de desktop

No Windows 10/11 x64, execute o comando abaixo no PowerShell.

```powershell
irm https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.ps1 | iex
```

No Linux ou macOS, execute o comando abaixo com seu usuário normal.

```sh
curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash
```

O instalador verifica a soma de verificação da versão e abre a página de configurações. Ele também pode instalar o aplicativo do celular via USB. Execute-o novamente para atualizar ou reparar a instalação, mantendo configurações e pareamentos.

### 2. Conecte o telefone

Abra a página de configurações na bandeja Windows ou visite o endereço abaixo. <http://127.0.0.1:8765>

- USB. Ative a depuração USB no celular, conecte um cabo de dados e autorize o computador. Use o instalador ou o botão de instalação do aplicativo no editor de configurações.
- Wi-Fi. Instale o APK da versão mais recente. Ative o Wi-Fi nas configurações do agente, salve e reinicie. Selecione o pareamento via Wi-Fi, leia o código QR e confirme que o mesmo código aparece nos dois dispositivos.

## Privacidade e segurança

O PhoneBox conecta o celular e o computador diretamente. Ele dispensa conta e não envia telemetria. Alguns widgets opcionais se conectam aos serviços que você configurar.

## Documentação

- [Instalação, assinatura e solução de problemas](SETUP.md)
- [Guia do usuário e galeria de widgets](GUIDE.md)
- [Temas, fontes e visualização](APPEARANCE.md)
- [Guia de Control Deck](CONTROL-DECK.md)
- [Desenvolvimento e testes](DEVELOPMENT.md)
- [Política de privacidade](../PRIVACY.md)
- [Changelog](../CHANGELOG.md)

## Licença

[PhoneBox é lançado sob a licença MIT.](../LICENSE)
