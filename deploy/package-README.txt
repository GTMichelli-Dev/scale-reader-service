Scale Reader Service - Windows install package
==============================================

Prebuilt and SELF-CONTAINED. The .NET runtime is bundled, so this PC needs no
.NET install, no SDK and no git.


INSTALL OR UPDATE
-----------------
Open an ADMIN command prompt in this folder and run:

    INSTALL.bat https://your-web-app-url

Use the web app's real address - the same one you type in a browser, with the
same scheme and port. A wrong URL leaves the service reconnecting forever.

The same command handles a fresh install and an update, and is safe to re-run.

    INSTALL.bat https://your-web-app-url -ResetDb
        Start from a clean database. DESTROYS the existing scale
        configuration, serial port settings and retained tares. A timestamped
        backup is taken first regardless.

    INSTALL.bat https://your-web-app-url -ServiceId site-a
        Name this reader, when a site runs more than one.

    powershell -ExecutionPolicy Bypass -File install-windows.ps1 -?
        All options, including -Port and -InstallDir.


WHAT IT DOES
------------
 1. Validates the arguments and finds the binaries.
 2. Stops the service and waits for it to really stop. (It holds its own .exe;
    copying too early fails with a file lock.)
 3. Backs up the database to the Desktop, timestamped.
 4. Copies the new binaries, leaving the database alone.
 5. Writes the web app URL into appsettings.json.
 6. Creates the service if missing, with AUTOMATIC STARTUP and set to restart
    on failure (5s, 15s, then every 60s). An existing service has its path
    corrected and startup set to automatic.
 7. Starts it and waits for the health endpoint to answer, failing loudly if
    it never does rather than reporting success over a dead service.
 8. Applies the ServiceId and ServerUrl through the API.

Step 8 is not redundant: appsettings.json only seeds the database while
ServerUrl is still the factory default, so on a machine that has been running
for months, editing the config file alone would change nothing.


THE DATABASE
------------
scalereaderservice.db lives in the application folder and holds the scale
configuration, the serial port or IP, the ServerUrl and the retained tares. It
is not part of this package - the existing one is kept and backed up. Use
-ResetDb only when you genuinely want to start over.

Schema changes apply themselves on first start, with existing rows intact.


AFTER INSTALLING
----------------
The installer prints the health result and the settings it applied. The
service should appear on the web app's Scale Management page.

    sc query ScaleReaderService
    http://localhost:5220/swagger
    http://localhost:5220/api/diagnostic     (raw frames from every scale)

Swagger listens on all interfaces, but Windows Firewall blocks it from other
machines unless you add an inbound rule for the port (5220 by default).


SETTING UP A SCALE
------------------
Add the scale on the web app's Scale Management page. If the weight does not
read correctly - "Wrong Units", a stuck zero, or a wildly wrong number - the
indicator's frame format does not match any built-in pattern. Use Auto-Detect:

  1. Edit the scale, set Active = No, Save.
     (An active scale's reader holds the port; Auto-Detect cannot open it.)
  2. Put a load on the scale so the weight is not sitting at zero - a reading
     that changes is far easier to locate in the frame.
  3. Edit again -> Auto-Detect. It listens for about 4 seconds.
  4. Check the captured frames and the proposed columns, adjust if needed -
     the preview updates as you type - then Save.
  5. Set Active = Yes, Save.

If Auto-Detect finds nothing, the message says which problem it is:
  - no bytes at all      -> wrong port/baud/parity, or the indicator is not
                            streaming (if it only replies when polled, put its
                            command in Request Command and detect again)
  - bytes but no frames  -> shows a hex sample; almost always baud/parity
  - port busy            -> names the scale holding it (see step 1)
