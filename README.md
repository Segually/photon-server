# photon-server

A self-hosted server alternative to Photon Cloud, designed for private servers compatible with PUN2 (UDP) clients.

## Overview

This project implements a server that speaks the Photon networking protocol, allowing PUN2 UDP clients to connect to a privately hosted server instead of Photon Cloud. It targets the common subset of the protocol used by PUN2 games over UDP.

## How It Was Built

The protocol implementation is based entirely on observational work:

- **Reverse engineering** — games that use Photon were analyzed to understand message structure, operation codes, and handshake behavior.
- **Packet sniffing** — network traffic between PUN2 clients and Photon Cloud was captured and inspected to document the wire protocol.

No Photon SDK source code was read, referenced, or used at any point in the development of this project. The implementation was derived purely from observed behavior and captured network data.

## Disclaimer

This project is not affiliated with, endorsed by, or connected to Exit Games in any way. Photon is a trademark of Exit Games GmbH.

This project does not ship any Photon SDK or Exit Games code. It is intended for private, non-commercial use only. You should not use this project for commercial purposes.
